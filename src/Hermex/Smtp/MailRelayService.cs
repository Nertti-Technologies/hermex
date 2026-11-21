using Hermex.Diagnostics;
using Hermex.Internal;
using Hermex.Storage;

namespace Hermex.Smtp;

/// <summary>The outcome of a relay attempt.</summary>
public sealed record MailRelayResult(bool Success, string? Error)
{
    /// <summary>
    /// <c>true</c> when the relay was refused by policy (the relay host allow-list, the recipient rules or an
    /// <see cref="HermexEvents.OnRelaying"/> hook) rather than failing.
    /// </summary>
    public bool Rejected { get; init; }

    public static MailRelayResult Ok() => new(true, null);
    public static MailRelayResult Fail(string error) => new(false, error);
    public static MailRelayResult Refused(string reason) => new(false, reason) { Rejected = true };
}

/// <summary>Forwards captured messages to a configured upstream SMTP server.</summary>
public interface IMailRelayService
{
    /// <summary>Whether relay is enabled in configuration.</summary>
    bool IsEnabled { get; }

    /// <summary>Forwards a stored message to the upstream SMTP server.</summary>
    Task<MailRelayResult> RelayAsync(long messageId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Forwards a stored message; <paramref name="automatic"/> tells the
    /// <see cref="HermexEvents.OnRelaying"/> hook whether this is automatic relay of new mail.
    /// </summary>
    Task<MailRelayResult> RelayAsync(long messageId, bool automatic, CancellationToken cancellationToken = default) =>
        RelayAsync(messageId, cancellationToken);
}

/// <summary>Default <see cref="IMailRelayService"/> backed by <see cref="SmtpRelayClient"/>.</summary>
internal sealed class MailRelayService : IMailRelayService
{
    private readonly IMailStore _store;
    private readonly HermexOptions _options;
    private readonly IHermexEventLog _eventLog;
    private readonly HermexEventDispatcher _events;

    public MailRelayService(IMailStore store, HermexOptions options, IHermexEventLog eventLog,
        HermexEventDispatcher events)
    {
        _store = store;
        _options = options;
        _eventLog = eventLog;
        _events = events;
    }

    public bool IsEnabled => _options.Relay.Enabled;

    public Task<MailRelayResult> RelayAsync(long messageId, CancellationToken cancellationToken = default) =>
        RelayAsync(messageId, automatic: false, cancellationToken);

    public async Task<MailRelayResult> RelayAsync(long messageId, bool automatic,
        CancellationToken cancellationToken = default)
    {
        if (!_options.Relay.Enabled)
            return MailRelayResult.Fail("Relay is not enabled.");

        var detail = await _store.GetMessageAsync(messageId, cancellationToken).ConfigureAwait(false);
        if (detail is null)
            return MailRelayResult.Fail($"Message #{messageId} was not found.");

        var raw = await _store.GetRawMessageAsync(messageId, cancellationToken).ConfigureAwait(false);
        if (raw is null)
            return MailRelayResult.Fail($"The raw content of message #{messageId} was not found.");

        var envelope = SplitRecipients(detail.EnvelopeTo);
        if (envelope.Count == 0)
            return MailRelayResult.Fail("The message has no envelope recipients to relay to.");

        // A sender the filter cannot vouch for is refused (empty is the null sender, <>).
        var from = detail.EnvelopeFrom.Trim();
        if (from.Length > 0)
        {
            if (HermexRelayOptions.NormalizeAddress(from) is not { } normalizedFrom)
            {
                _eventLog.Warning("Relay", $"Message #{messageId} was not relayed: the sender is not a plain address.");
                return MailRelayResult.Refused("The message's sender is not a plain address, so it is not relayed.");
            }
            from = normalizedFrom;
        }

        // Recipient rules (ADR-0003 D14) on every relay path, manual and automatic: host and dashboard layers.
        var recipients = HermexRelayOptions.FilterRecipients(envelope, _options.Relay.RecipientRules());
        if (recipients.Count == 0)
        {
            _eventLog.Warning("Relay", $"Message #{messageId} was not relayed: no recipient passes the relay recipient rules.");
            return MailRelayResult.Refused("No recipient of this message passes the relay recipient rules.");
        }
        if (recipients.Count < envelope.Count)
            _eventLog.Info("Relay", $"Message #{messageId}: {envelope.Count - recipients.Count} of {envelope.Count} recipient(s) left out by the relay recipient rules.");

        // One snapshot is checked, shown to the hook and used to connect, so a concurrent settings
        // change cannot redirect the message to a host that was never checked or approved.
        var target = _options.Relay.Snapshot();

        // Settings are validated against the allow-list, but options can also be changed in
        // code after startup, so check again right before connecting.
        if (!_options.Relay.IsHostAllowed(target.Host))
        {
            _eventLog.Warning("Relay", $"Message #{messageId} was not relayed: the relay host is not allowed.");
            return MailRelayResult.Refused("The relay host is not in the host application's list of allowed relay hosts.");
        }

        var relaying = new HermexRelayingContext
        {
            MessageId = messageId,
            Host = target.Host,
            Port = target.Port,
            From = from,
            Recipients = recipients.ToArray(), // a copy: a hook cannot add recipients after the filter
            Automatic = automatic,
            HttpContext = _events.HttpContext,
            CancellationToken = cancellationToken,
        };
        if (!await _events.RelayingAsync(relaying).ConfigureAwait(false))
        {
            _eventLog.Info("Relay", $"Message #{messageId} was not relayed: a host hook rejected it.");
            return MailRelayResult.Refused(relaying.RejectionReason!);
        }

        try
        {
            await SmtpRelayClient.SendAsync(target, from, recipients, raw, cancellationToken)
                .ConfigureAwait(false);
            _eventLog.Info("Relay", $"Message #{messageId} relayed to {target.Host}:{target.Port}.");
            return MailRelayResult.Ok();
        }
        catch (Exception ex)
        {
            // An upstream reply can quote addresses (550 <user@x>: rejected), so the log keeps only its code; the
            // caller still gets the full text.
            var logged = ex is SmtpReplyException reply
                ? $"the upstream server replied {reply.ReplyCode} (expected {reply.ExpectedCode})"
                : ex is HermexException ? ex.Message : ex.GetType().Name;
            _eventLog.Error("Relay", $"Failed to relay message #{messageId}: {logged}.");
            return MailRelayResult.Fail(ex.Message);
        }
    }

    private static List<string> SplitRecipients(string envelopeTo)
    {
        var result = new List<string>();
        foreach (var part in envelopeTo.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            result.Add(part);
        return result;
    }
}
