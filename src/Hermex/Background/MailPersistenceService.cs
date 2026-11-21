using System.Threading.Channels;
using Hermex.Diagnostics;
using Hermex.Internal;
using Hermex.Mime;
using Hermex.Realtime;
using Hermex.Smtp;
using Hermex.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Hermex.Background;

/// <summary>
/// Drains the ingestion queue, parses each message off the SMTP path, persists batches in a
/// single transaction and broadcasts the result live. Running here — rather than on the SMTP
/// connection thread — is what keeps mail capture fast and the host application unaffected.
/// </summary>
internal sealed class MailPersistenceService : BackgroundService
{
    private const int LogBatchSize = 128;

    // Estimate: one warning per this many dropped stored-message events keeps a sustained
    // overload visible without flooding the host's log.
    private const long DroppedEventWarningInterval = 1_000;

    private readonly MailWriteQueue _queue;
    private readonly MessageArrivals _arrivals;
    private readonly WebhookService _webhooks;
    private readonly IMailStore _store;
    private readonly IInboxNotifier _notifier;
    private readonly IMailRelayService _relay;
    private readonly IHermexEventLog _eventLog;
    private readonly HermexOptions _options;
    private readonly HermexEventDispatcher _events;
    private readonly ILogger<MailPersistenceService> _logger;

    // OnMessageStored events wait here for a single consumer, so a slow host hook never slows
    // persistence or capture. Bounded by WriteQueueCapacity: worst case that many small contexts
    // (ids, addresses, subject) in memory; beyond it events are dropped and counted.
    private readonly Channel<HermexMessageStoredContext> _storedEvents;
    private long _droppedStoredEvents;

    public MailPersistenceService(
        MailWriteQueue queue,
        IMailStore store,
        IInboxNotifier notifier,
        IMailRelayService relay,
        IHermexEventLog eventLog,
        HermexOptions options,
        HermexEventDispatcher events,
        MessageArrivals arrivals,
        WebhookService webhooks,
        ILogger<MailPersistenceService> logger)
    {
        _webhooks = webhooks;
        _arrivals = arrivals;
        _queue = queue;
        _store = store;
        _notifier = notifier;
        _relay = relay;
        _eventLog = eventLog;
        _options = options;
        _events = events;
        _logger = logger;
        _storedEvents = Channel.CreateBounded<HermexMessageStoredContext>(
            new BoundedChannelOptions(Math.Max(1, options.WriteQueueCapacity))
            {
                SingleReader = true,
                FullMode = BoundedChannelFullMode.Wait, // TryWrite then fails, so drops can be counted
            });
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _store.InitializeAsync(stoppingToken).ConfigureAwait(false);

        // Messages, logs and stored-message hooks are drained on independent loops so a slow
        // batch of one never stalls the others.
        await Task.WhenAll(
            ProcessMessagesAsync(stoppingToken),
            ProcessLogsAsync(stoppingToken),
            ProcessStoredEventsAsync()).ConfigureAwait(false);
    }

    private async Task ProcessMessagesAsync(CancellationToken stoppingToken)
    {
        try
        {
            await DrainMessagesAsync(stoppingToken).ConfigureAwait(false);
        }
        finally
        {
            // No more messages, whether stopping or faulted: let the hook consumer finish what is
            // queued and stop, so a fault still surfaces instead of WhenAll waiting forever.
            _storedEvents.Writer.TryComplete();
        }
    }

    private async Task DrainMessagesAsync(CancellationToken stoppingToken)
    {
        var reader = _queue.MessageReader;
        var batch = new List<ReceivedMessage>(Math.Max(1, _options.PersistenceBatchSize));

        try
        {
            while (await reader.WaitToReadAsync(stoppingToken).ConfigureAwait(false))
            {
                batch.Clear();
                while (batch.Count < _options.PersistenceBatchSize && reader.TryRead(out var message))
                    batch.Add(message);

                if (batch.Count > 0)
                    await PersistMessageBatchAsync(batch, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }

        // Flush whatever is still queued so nothing is lost on shutdown.
        var remaining = new List<ReceivedMessage>();
        while (reader.TryRead(out var message))
            remaining.Add(message);
        if (remaining.Count > 0)
        {
            try { await PersistMessageBatchAsync(remaining, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogError(ex, "Hermex: failed to flush messages during shutdown."); }
        }
    }

    private async Task PersistMessageBatchAsync(List<ReceivedMessage> batch, CancellationToken cancellationToken)
    {
        var records = new List<MessageRecord>(batch.Count);
        var addresses = new List<(string[] To, string[] Cc)>(batch.Count); // for webhook notices (ADR-0003 D4)

        foreach (var received in batch)
        {
            MimeMessage mime;
            try
            {
                mime = MimeParser.Parse(received.RawData);
            }
            catch (Exception ex)
            {
                // Parsing should never throw, but never lose a message if it does.
                _eventLog.Error("Storage",
                    $"Could not parse a message from {received.RemoteEndPoint}: {ex.Message}", received.SessionId);
                mime = MimeParser.Parse(Array.Empty<byte>());
            }

            records.Add(BuildRecord(received, mime));
            addresses.Add((mime.To.Select(a => a.Address).ToArray(), mime.Cc.Select(a => a.Address).ToArray()));
        }

        try
        {
            await _store.AddMessagesAsync(records, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Hermex: failed to persist a batch of {Count} message(s).", records.Count);
            _eventLog.Error("Storage", $"Failed to persist {records.Count} message(s): {ex.Message}");
            return;
        }

        // Committed: wake wait-API callers (ADR-0003 D6) before the slower dashboard notifications.
        _arrivals.Pulse();
        for (var i = 0; i < records.Count; i++)
            _webhooks.Enqueue(records[i], addresses[i].To, addresses[i].Cc); // never blocks capture

        foreach (var record in records)
            await _notifier.MessageReceivedAsync(ToSummary(record)).ConfigureAwait(false);

        try
        {
            var stats = await _store.GetStatsAsync(cancellationToken).ConfigureAwait(false);
            await _notifier.StatsChangedAsync(stats).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Hermex: could not refresh statistics after a batch.");
        }

        foreach (var record in records)
            QueueStoredEvent(record, cancellationToken);

        // Automatic relay runs off the persistence path; RelayAsync logs its own failures.
        if (_options.Relay is { Enabled: true, AutomaticRelay: true })
        {
            foreach (var record in records)
            {
                var id = record.Id;
                _ = _relay.RelayAsync(id, automatic: true, CancellationToken.None);
            }
        }
    }

    private void QueueStoredEvent(MessageRecord record, CancellationToken cancellationToken)
    {
        var context = new HermexMessageStoredContext
        {
            MessageId = record.Id,
            From = record.FromAddress,
            To = record.Recipients,
            Subject = record.Subject,
            SizeBytes = record.RawSize,
            CancellationToken = cancellationToken,
        };
        if (_storedEvents.Writer.TryWrite(context))
            return;

        var dropped = Interlocked.Increment(ref _droppedStoredEvents);
        if (dropped % DroppedEventWarningInterval == 1)
        {
            _logger.LogWarning(
                "Hermex: the OnMessageStored hook is falling behind; {Dropped} event(s) dropped so far.", dropped);
        }
    }

    private async Task ProcessStoredEventsAsync()
    {
        // Drains until the message loop completes the writer, so events for mail flushed during
        // shutdown are still delivered; the host's shutdown timeout bounds how long that takes.
        var reader = _storedEvents.Reader;
        while (await reader.WaitToReadAsync().ConfigureAwait(false))
        {
            while (reader.TryRead(out var context))
                await _events.MessageStoredAsync(context).ConfigureAwait(false);
        }
    }

    private async Task ProcessLogsAsync(CancellationToken stoppingToken)
    {
        var reader = _queue.LogReader;
        var batch = new List<HermexLogEntry>(LogBatchSize);

        try
        {
            while (await reader.WaitToReadAsync(stoppingToken).ConfigureAwait(false))
            {
                batch.Clear();
                while (batch.Count < LogBatchSize && reader.TryRead(out var entry))
                    batch.Add(entry);

                if (batch.Count == 0)
                    continue;

                try
                {
                    await _store.AddLogsAsync(batch, stoppingToken).ConfigureAwait(false);
                    foreach (var entry in batch)
                        await _notifier.LogAddedAsync(entry).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Hermex: failed to persist diagnostic log entries.");
                }
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
    }

    private static MessageRecord BuildRecord(ReceivedMessage received, MimeMessage mime)
    {
        var fromDisplay = !string.IsNullOrWhiteSpace(mime.From?.DisplayName)
            ? mime.From!.DisplayName
            : mime.From?.Address ?? received.MailFrom;

        var toDisplay = mime.To.Count > 0
            ? MimeAddress.FormatList(mime.To)
            : string.Join(", ", received.Recipients);

        return new MessageRecord
        {
            SessionId = received.SessionId,
            ReceivedAtUtc = received.ReceivedAtUtc,
            EnvelopeFrom = received.MailFrom,
            EnvelopeTo = string.Join(", ", received.Recipients),
            FromAddress = mime.From?.Address ?? received.MailFrom,
            FromDisplay = string.IsNullOrWhiteSpace(fromDisplay) ? "(unknown sender)" : fromDisplay,
            ToDisplay = toDisplay,
            CcDisplay = mime.Cc.Count > 0 ? MimeAddress.FormatList(mime.Cc) : null,
            Subject = string.IsNullOrEmpty(mime.Subject) ? "(no subject)" : mime.Subject!,
            HasHtml = mime.HasHtmlBody,
            HasText = mime.HasTextBody,
            RawSize = received.RawData.Length,
            AttachmentCount = mime.Attachments.Count,
            RemoteEndPoint = received.RemoteEndPoint,
            Recipients = received.Recipients,
            SecuredWithTls = received.SecuredWithTls,
            Transcript = received.Transcript,
            RawData = received.RawData,
        };
    }

    private static MessageSummary ToSummary(MessageRecord record) => new()
    {
        Id = record.Id,
        ReceivedAtUtc = record.ReceivedAtUtc,
        From = record.FromDisplay,
        FromAddress = record.FromAddress,
        To = record.ToDisplay,
        Subject = record.Subject,
        HasHtml = record.HasHtml,
        AttachmentCount = record.AttachmentCount,
        HasAttachments = record.AttachmentCount > 0,
        RawSize = record.RawSize,
        IsRead = false,
    };
}
