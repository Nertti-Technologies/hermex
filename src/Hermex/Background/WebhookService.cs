using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Channels;
using Hermex.Internal;
using Hermex.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Hermex.Background;

/// <summary>One line of the webhook delivery log. Never holds the body, headers, secret or the URL's query.</summary>
internal sealed record WebhookDelivery(
    DateTimeOffset AtUtc, string WebhookId, long MessageId, int Endpoint, int Attempt,
    int? StatusCode, long DurationMs, string Outcome, string? Error);

/// <summary>The state of one endpoint, as shown on the Logs page.</summary>
internal sealed record WebhookEndpointStatus(int Index, string Target, string State, int ConsecutiveFailures);

/// <summary>
/// Sends the webhook notices of ADR-0003 D4. <see cref="Enqueue"/> never blocks mail capture: notices go to a bounded
/// queue that drops the oldest when full. Each notice is retried on its own schedule; at most
/// <see cref="HermexWebhookOptions.QueueCapacity"/> notices are queued and as many again in retry, and at most
/// <see cref="MaxConcurrentSends"/> requests are in flight. Delivery state is in memory and lost on restart.
/// </summary>
internal sealed class WebhookService : BackgroundService
{
    /// <summary>Requests in flight at once (estimate: enough for a handful of local receivers).</summary>
    private const int MaxConcurrentSends = 4;

    /// <summary>Delivery log entries kept (ADR-0003 D4, estimate).</summary>
    public const int LogCapacity = 500;

    /// <summary>Consecutive failures that pause an endpoint (ADR-0003 D4, estimate).</summary>
    public const int PauseAfterFailures = 20;

    /// <summary>Addresses per list in a notice, and characters per address and subject (estimate: keeps the body near
    /// ADR-0003 D4's 20 kB: 3 lists × 20 × 320 + 512 ≈ 19.7 kB; 320 is a generous bound over RFC 5321's 256-octet path).</summary>
    private const int MaxAddressesPerList = 20, MaxAddressLength = 320, MaxSubjectLength = 512;

    /// <summary>Longest Retry-After honoured (ADR-0003 D4).</summary>
    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromMinutes(10);

    /// <summary>Delays before attempts 2, 3, … (ADR-0003 D4, estimate); the last repeats.</summary>
    private static readonly TimeSpan[] DefaultRetryDelays =
        { TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(10) };

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HermexWebhookOptions _options;
    private readonly HermexEventDispatcher _events;
    private readonly ILogger<WebhookService> _logger;
    private readonly Channel<Job> _queue;
    private readonly SemaphoreSlim _pending;
    private readonly SemaphoreSlim _sending = new(MaxConcurrentSends);
    private readonly HttpClient _http;
    private readonly EndpointState[] _endpoints;
    private readonly Queue<WebhookDelivery> _log = new();
    private readonly object _logLock = new();
    private long _dropped;
    private long _skipped;
    private readonly ConcurrentDictionary<Task, byte> _inFlight = new();

    public WebhookService(HermexOptions options, HermexEventDispatcher events, IHostEnvironment environment,
        ILogger<WebhookService> logger)
    {
        _options = options.Webhooks;
        _events = events;
        _logger = logger;
        _endpoints = _options.Endpoints.Select((e, i) => new EndpointState(i, e)).ToArray();
        _pending = new SemaphoreSlim(_options.QueueCapacity);
        _queue = Channel.CreateBounded<Job>(
            new BoundedChannelOptions(_options.QueueCapacity) { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest },
            _ => Interlocked.Increment(ref _dropped)); // counted, not logged: rows would push out the failures

        var allowPrivate = WebhookNetworkGuard.AllowsPrivate(_options.NetworkPolicy, environment.IsDevelopment());
        _http = new HttpClient(new SocketsHttpHandler
        {
            // Every connection goes through the guard, on the address actually connected to.
            ConnectCallback = (context, ct) => WebhookNetworkGuard.ConnectAsync(context.DnsEndPoint, allowPrivate, ct),
            AllowAutoRedirect = false,      // a redirect could point anywhere; 3xx counts as a failure
            UseProxy = false,               // a proxy would connect on our behalf, past the guard
            UseCookies = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2), // estimate: re-resolve names now and then
        })
        { Timeout = Timeout.InfiniteTimeSpan }; // per-attempt timeout below
    }

    /// <summary>True when at least one endpoint is configured.</summary>
    public bool Enabled => _endpoints.Length > 0;

    /// <summary>Notices dropped because the queue was full.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>Notices not sent because their endpoint was paused or disabled.</summary>
    public long Skipped => Interlocked.Read(ref _skipped);

    /// <summary>Queues a notice for every active endpoint. Never blocks.</summary>
    public void Enqueue(MessageRecord record, IReadOnlyList<string> to, IReadOnlyList<string> cc)
    {
        if (_endpoints.Length == 0)
            return;

        var webhookId = "msg_" + Guid.NewGuid().ToString("N");
        var body = JsonSerializer.SerializeToUtf8Bytes(new
        {
            type = "message.received",
            timestamp = DateTimeOffset.UtcNow,
            data = new
            {
                id = record.Id,
                receivedAtUtc = record.ReceivedAtUtc,
                mailboxes = Cap(record.Recipients),
                from = TextLimits.Truncate(record.FromAddress, MaxAddressLength),
                to = Cap(to),
                cc = Cap(cc),
                truncated = record.Recipients.Count > MaxAddressesPerList || to.Count > MaxAddressesPerList
                    || cc.Count > MaxAddressesPerList || record.Subject.Length > MaxSubjectLength,
                subject = TextLimits.Truncate(record.Subject, MaxSubjectLength),
                size = record.RawSize,
                hasHtml = record.HasHtml,
                attachmentCount = record.AttachmentCount,
                apiPath = $"{HermexRoutes.ApiBase}/messages/{record.Id}",
            },
        }, Json);

        foreach (var endpoint in _endpoints)
        {
            var job = new Job(endpoint, webhookId, record.Id, body);
            if (endpoint.State != EndpointState.Active)
                Interlocked.Increment(ref _skipped);
            else
                _queue.Writer.TryWrite(job); // DropOldest: always succeeds; drops are counted in the callback
        }
    }

    private static string[] Cap(IReadOnlyList<string> addresses) =>
        addresses.Take(MaxAddressesPerList).Select(a => TextLimits.Truncate(a, MaxAddressLength)).ToArray();

    /// <summary>Resumes a paused or disabled endpoint. Returns false for an unknown index.</summary>
    public bool Resume(int index)
    {
        if (index < 0 || index >= _endpoints.Length)
            return false;
        _endpoints[index].Resume();
        return true;
    }

    public IReadOnlyList<WebhookEndpointStatus> Endpoints =>
        _endpoints.Select(e => new WebhookEndpointStatus(e.Index, e.Target, e.State, e.ConsecutiveFailures)).ToArray();

    public IReadOnlyList<WebhookDelivery> Deliveries
    {
        get
        {
            lock (_logLock)
                return _log.Reverse().ToArray(); // newest first
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_endpoints.Length == 0)
            return;

        _logger.LogInformation("Hermex: webhooks enabled for {Count} endpoint(s): {Targets}.",
            _endpoints.Length, string.Join(", ", _endpoints.Select(e => e.Target)));
        try
        {
            while (true)
            {
                await _pending.WaitAsync(stoppingToken).ConfigureAwait(false);
                var job = await _queue.Reader.ReadAsync(stoppingToken).ConfigureAwait(false);
                var delivery = Task.Run(() => DeliverAsync(job, stoppingToken), CancellationToken.None);
                _inFlight.TryAdd(delivery, 0);
                _ = delivery.ContinueWith(t => _inFlight.TryRemove(t, out _), TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down: queued and retrying notices are dropped (in-memory delivery, ADR-0003 D4).
        }
    }

    /// <summary>Runs every attempt of one notice. Releases its pending slot when done.</summary>
    private async Task DeliverAsync(Job job, CancellationToken stoppingToken)
    {
        try
        {
            for (var attempt = 1; attempt <= _options.MaxAttempts; attempt++)
            {
                if (job.Endpoint.State != EndpointState.Active)
                {
                    Interlocked.Increment(ref _skipped);
                    return;
                }

                var result = await AttemptAsync(job, attempt, stoppingToken).ConfigureAwait(false);
                if (result.Done)
                    return;

                if (attempt < _options.MaxAttempts)
                    await Task.Delay(result.RetryAfter ?? NextDelay(attempt), stoppingToken).ConfigureAwait(false);
            }
            _logger.LogWarning("Hermex: webhook {WebhookId} to endpoint {Endpoint} failed after {Attempts} attempt(s).",
                job.WebhookId, job.Endpoint.Index, _options.MaxAttempts);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Hermex: webhook {WebhookId} stopped unexpectedly.", job.WebhookId);
        }
        finally
        {
            _pending.Release();
        }
    }

    private async Task<(bool Done, TimeSpan? RetryAfter)> AttemptAsync(Job job, int attempt, CancellationToken stoppingToken)
    {
        var endpoint = job.Endpoint;
        var sending = new HermexWebhookSendingContext
        {
            EndpointIndex = endpoint.Index,
            Url = endpoint.Url,
            WebhookId = job.WebhookId,
            MessageId = job.MessageId,
            Attempt = attempt,
            CancellationToken = stoppingToken,
        };
        if (!await _events.WebhookSendingAsync(sending).ConfigureAwait(false))
        {
            Record(job, attempt, null, 0, "Vetoed", null);
            return (true, null);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint.Url)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower, // never HTTP/3: ConnectCallback does not cover QUIC
            Content = new ByteArrayContent(job.Body),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        if (!TryAddHostHeaders(request, sending.Headers))
        {
            Record(job, attempt, null, 0, "Vetoed", "Invalid header from OnWebhookSending");
            return (true, null);
        }
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        request.Headers.TryAddWithoutValidation("webhook-id", job.WebhookId);
        request.Headers.TryAddWithoutValidation("webhook-timestamp", timestamp.ToString(CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation("webhook-signature", WebhookSigner.Sign(job.WebhookId, timestamp, job.Body, endpoint.Keys));

        await _sending.WaitAsync(stoppingToken).ConfigureAwait(false);
        var watch = Stopwatch.StartNew(); // the HTTP attempt only, not the wait for a send slot
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            timeout.CancelAfter(_options.Timeout);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
            var status = (int)response.StatusCode;

            if (status is >= 200 and < 300)
            {
                endpoint.Succeeded();
                Record(job, attempt, status, watch.ElapsedMilliseconds, "Delivered", null);
                return (true, null);
            }
            if (response.StatusCode == HttpStatusCode.Gone)
            {
                endpoint.Disable();
                Record(job, attempt, status, watch.ElapsedMilliseconds, "Disabled", "410 Gone: endpoint disabled until resumed");
                _logger.LogWarning("Hermex: webhook endpoint {Endpoint} answered 410 Gone and is disabled.", endpoint.Index);
                return (true, null);
            }

            Fail(job, attempt, status, watch.ElapsedMilliseconds, status is >= 300 and < 400 ? "Redirects are not followed" : null);
            var retryAfter = response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable
                ? RetryAfter(response.Headers.RetryAfter) : null;
            return (false, retryAfter);
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            Fail(job, attempt, null, watch.ElapsedMilliseconds, "Timed out");
            return (false, null);
        }
        catch (HttpRequestException ex) when (IsBlocked(ex))
        {
            // Deterministic: retrying would be refused again. Counts toward pausing the endpoint.
            Fail(job, attempt, null, watch.ElapsedMilliseconds, "Blocked by NetworkPolicy", outcome: "Blocked");
            return (true, null);
        }
        catch (HttpRequestException ex)
        {
            Fail(job, attempt, null, watch.ElapsedMilliseconds, ex.HttpRequestError.ToString());
            return (false, null);
        }
        finally
        {
            _sending.Release();
        }
    }

    private static bool IsBlocked(Exception ex)
    {
        for (var e = ex.InnerException; e is not null; e = e.InnerException)
            if (e is WebhookBlockedException)
                return true;
        return false;
    }

    private void Fail(Job job, int attempt, int? status, long ms, string? error, string outcome = "Failed")
    {
        Record(job, attempt, status, ms, outcome, error);
        if (job.Endpoint.Failed())
            _logger.LogWarning("Hermex: webhook endpoint {Endpoint} is paused after {Count} consecutive failures.",
                job.Endpoint.Index, PauseAfterFailures);
    }

    private TimeSpan NextDelay(int attempt)
    {
        var delays = _options.RetryDelaysOverride ?? DefaultRetryDelays;
        var delay = delays[Math.Min(attempt - 1, delays.Count - 1)];
        return delay * (0.8 + (Random.Shared.NextDouble() * 0.4)); // ±20 % jitter (ADR-0003 D4)
    }

    private static TimeSpan? RetryAfter(RetryConditionHeaderValue? value)
    {
        var delay = value?.Delta ?? (value?.Date is { } date ? date - DateTimeOffset.UtcNow : null);
        if (delay is null)
            return null;
        return delay < TimeSpan.Zero ? TimeSpan.Zero : delay > MaxRetryAfter ? MaxRetryAfter : delay;
    }

    private static readonly HashSet<string> ReservedHeaders = new(StringComparer.OrdinalIgnoreCase)
        { "content-type", "content-length", "host", "transfer-encoding", "connection" };

    private static bool TryAddHostHeaders(HttpRequestMessage request, IDictionary<string, string> headers)
    {
        foreach (var (name, value) in headers)
        {
            if (name.Length == 0 || ReservedHeaders.Contains(name) || name.StartsWith("webhook-", StringComparison.OrdinalIgnoreCase)
                || name.Any(c => c <= ' ' || c >= 127 || "()<>@,;:\\\"/[]?={}".Contains(c, StringComparison.Ordinal))
                || value is null || value.Any(c => c is < ' ' or > '~') // printable ASCII only (RFC 9110 §5.5)
                || !request.Headers.TryAddWithoutValidation(name, value))
                return false;
        }
        return true;
    }

    private void Record(Job job, int attempt, int? status, long ms, string outcome, string? error)
    {
        var entry = new WebhookDelivery(DateTimeOffset.UtcNow, job.WebhookId, job.MessageId, job.Endpoint.Index,
            attempt, status, ms, outcome, error);
        lock (_logLock)
        {
            _log.Enqueue(entry);
            while (_log.Count > LogCapacity)
                _log.Dequeue();
        }
    }

    /// <summary>Stops reading, then waits for deliveries in flight (they see the stop and end) before disposal.</summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Task.WhenAll(_inFlight.Keys).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The host's shutdown timeout ran out; the remaining deliveries are abandoned.
        }
    }

    // The semaphores are not disposed: a delivery abandoned at a shutdown timeout may still release one, and
    // SemaphoreSlim holds no unmanaged resource unless AvailableWaitHandle is used (it is not).
    public override void Dispose()
    {
        _http.Dispose();
        base.Dispose();
    }

    private sealed record Job(EndpointState Endpoint, string WebhookId, long MessageId, byte[] Body);

    /// <summary>Per-endpoint state. Transitions: Active ⇄ Paused (failures / resume), Active → Disabled (410) → Active (resume).</summary>
    private sealed class EndpointState
    {
        public const string Active = "Active", Paused = "Paused", Disabled = "Disabled";

        private readonly object _lock = new();
        private string _state = Active;
        private int _failures;

        public EndpointState(int index, HermexWebhookEndpoint endpoint)
        {
            Index = index;
            Url = endpoint.Url!;
            // Shown in logs and on the Logs page: scheme, host and port only. Some services carry a token in the
            // path (…/hooks/<id>/<token>) or the query, so neither is ever shown.
            Target = Url.GetLeftPart(UriPartial.Authority) + (Url.AbsolutePath.Length > 1 ? "/…" : "/");
            Keys = new[] { endpoint.Secret, endpoint.PreviousSecret }
                .Where(s => s is not null)
                .Select(s => WebhookSigner.TryDecodeSecret(s, out var key) ? key : throw new InvalidOperationException("Validated secret did not decode."))
                .ToArray();
        }

        public int Index { get; }
        public Uri Url { get; }
        public string Target { get; }
        public IReadOnlyList<byte[]> Keys { get; }

        public string State { get { lock (_lock) return _state; } }
        public int ConsecutiveFailures { get { lock (_lock) return _failures; } }

        public void Succeeded() { lock (_lock) _failures = 0; }

        /// <summary>Counts a failure; true when this one paused the endpoint.</summary>
        public bool Failed()
        {
            lock (_lock)
            {
                _failures++;
                if (_state != Active || _failures < PauseAfterFailures)
                    return false;
                _state = Paused;
                return true;
            }
        }

        public void Disable() { lock (_lock) _state = Disabled; }

        public void Resume() { lock (_lock) { _state = Active; _failures = 0; } }
    }
}
