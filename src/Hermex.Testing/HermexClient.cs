using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Hermex.Testing;

/// <summary>
/// Reads captured mail from a running Hermex over its HTTP API (ADR-0003 D7). Pass an <see cref="HttpClient"/>
/// whose base address is the host's root, e.g. <c>WebApplicationFactory.CreateClient()</c> (end the address with
/// "/" when the app runs under a path base). Waiting needs the Viewer tier, <see cref="DeleteAllAsync"/> the
/// Manager tier, when the host sets tier policies.
/// </summary>
public sealed class HermexClient
{
    /// <summary>Longest single server-side wait the client asks for. The server allows 120 s; 60 s keeps each request
    /// well inside <see cref="HttpClient"/>'s default 100 s timeout. Longer waits use several requests.</summary>
    private const int MaxRequestWaitSeconds = 60;

    // Server-owned names (Hermex's HermexRoutes.RequestHeader / CursorHeader / ApiBase); released clients depend
    // on them, so the server must not rename them. Relative, so a base address with a path base is kept.
    private const string RequestHeader = "X-Hermex-Request"; // the dashboard's CSRF header; harmless when unused
    private const string CursorHeader = "X-Hermex-After";
    private const string Api = "hermex/api";

    /// <summary>Margin left between a server-side wait and the caller's <see cref="HttpClient.Timeout"/> (estimate:
    /// covers the query and the response on a slow machine).</summary>
    private static readonly TimeSpan ClientTimeoutMargin = TimeSpan.FromSeconds(5);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;

    /// <summary>Creates a client.</summary>
    /// <param name="http">Client whose base address is the Hermex host's root.</param>
    public HermexClient(HttpClient http)
    {
        ArgumentNullException.ThrowIfNull(http);
        _http = http;
    }

    /// <summary>
    /// Returns the current cursor: the newest message id, or 0. Take it before triggering an email and pass it as
    /// <see cref="HermexMessageCriteria.After"/>, so only mail sent afterwards matches.
    /// </summary>
    public async Task<long> MarkAsync(CancellationToken cancellationToken = default)
    {
        var page = await GetJsonAsync<Page>($"{Api}/messages?page=1&pageSize=1", cancellationToken).ConfigureAwait(false);
        return page?.Items is { Count: > 0 } items ? items[0].Id : 0;
    }

    /// <summary>
    /// Waits until a message matching <paramref name="criteria"/> is stored and returns the oldest such message.
    /// The server pushes the arrival; nothing polls. Throws <see cref="HermexWaitTimeoutException"/> on timeout.
    /// </summary>
    /// <param name="criteria">What to match; null matches any new message.</param>
    /// <param name="timeout">How long to wait in total; default 30 seconds.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    public async Task<HermexMessage> WaitForMessageAsync(
        HermexMessageCriteria? criteria = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        criteria ??= new HermexMessageCriteria();
        var total = timeout ?? TimeSpan.FromSeconds(30);
        if (total <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout), "The timeout must be positive.");

        var deadline = DateTimeOffset.UtcNow + total;
        var after = criteria.After;
        while (true)
        {
            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
                throw new HermexWaitTimeoutException($"No message matching {criteria} arrived within {total}.")
                    { Criteria = criteria.ToString(), Timeout = total };

            var seconds = (int)Math.Clamp(Math.Ceiling(remaining.TotalSeconds), 1, MaxSecondsPerRequest());
            using var request = Request(HttpMethod.Get, WaitUrl(criteria, after, seconds));
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.OK)
                return (await response.Content.ReadFromJsonAsync<HermexMessage>(Json, cancellationToken).ConfigureAwait(false))!;

            // 204: this request timed out. Keep the cursor the server used so nothing stored meanwhile is skipped.
            if (after is null && response.Headers.TryGetValues(CursorHeader, out var values)
                && long.TryParse(values.FirstOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out var cursor))
                after = cursor;
        }
    }

    /// <summary>Returns a message with its bodies and headers, or null when it does not exist. Marks it read.</summary>
    public Task<HermexMessageDetail?> GetMessageAsync(long id, CancellationToken cancellationToken = default) =>
        GetJsonAsync<HermexMessageDetail>($"{Api}/messages/{id}", cancellationToken);

    /// <summary>Returns the HTML body as the dashboard previews it (cid: links point at Hermex), or null.</summary>
    public async Task<string?> GetHtmlAsync(long id, CancellationToken cancellationToken = default)
    {
        using var request = Request(HttpMethod.Get, $"{Api}/messages/{id}/html");
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Returns each unique link in the HTML body, or an empty list when there is none.</summary>
    public async Task<IReadOnlyList<HermexLink>> GetLinksAsync(long id, CancellationToken cancellationToken = default)
    {
        var checks = await GetJsonAsync<Checks>($"{Api}/messages/{id}/checks", cancellationToken).ConfigureAwait(false);
        return checks?.Html?.Links ?? (IReadOnlyList<HermexLink>)Array.Empty<HermexLink>();
    }

    /// <summary>Deletes every captured message and returns how many were deleted. Needs the Manager tier.</summary>
    public async Task<int> DeleteAllAsync(CancellationToken cancellationToken = default)
    {
        using var request = Request(HttpMethod.Delete, $"{Api}/messages");
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        var result = await response.Content.ReadFromJsonAsync<ClearResult>(Json, cancellationToken).ConfigureAwait(false);
        return result?.Cleared ?? 0;
    }

    /// <summary>The longest wait one request may ask for: 60 s, and inside the caller's HttpClient.Timeout.</summary>
    private int MaxSecondsPerRequest()
    {
        if (_http.Timeout == Timeout.InfiniteTimeSpan)
            return MaxRequestWaitSeconds;
        var fits = (int)Math.Floor((_http.Timeout - ClientTimeoutMargin).TotalSeconds);
        return Math.Clamp(fits, 1, MaxRequestWaitSeconds);
    }

    private static string WaitUrl(HermexMessageCriteria criteria, long? after, int seconds)
    {
        var url = new StringBuilder(Api).Append("/messages/wait?timeout=").Append(seconds.ToString(CultureInfo.InvariantCulture));
        void Add(string name, string? value)
        {
            if (!string.IsNullOrEmpty(value))
                url.Append('&').Append(name).Append('=').Append(Uri.EscapeDataString(value));
        }
        Add("to", criteria.To);
        Add("from", criteria.From);
        Add("subject", criteria.Subject);
        Add("mailbox", criteria.Mailbox);
        if (after is { } cursor)
            url.Append("&after=").Append(cursor.ToString(CultureInfo.InvariantCulture));
        return url.ToString();
    }

    private async Task<T?> GetJsonAsync<T>(string url, CancellationToken cancellationToken) where T : class
    {
        using var request = Request(HttpMethod.Get, url);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken).ConfigureAwait(false);
    }

    private static HttpRequestMessage Request(HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, new Uri(url, UriKind.RelativeOrAbsolute));
        request.Headers.Add(RequestHeader, "1");
        return request;
    }

    /// <summary>Throws with the server's reason (status and the short error body) on failure.</summary>
    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return;
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (body.Length > 300) // estimate: enough for Hermex's error messages
            body = body[..300] + "…";
        throw new HttpRequestException(
            $"Hermex returned {(int)response.StatusCode} {response.ReasonPhrase}{(body.Length > 0 ? ": " + body : "")}",
            null, response.StatusCode);
    }

    private sealed record Page(IReadOnlyList<HermexMessage>? Items);

    private sealed record Checks(LinkReport? Html);

    private sealed record LinkReport(IReadOnlyList<HermexLink>? Links);

    private sealed record ClearResult(int Cleared);
}
