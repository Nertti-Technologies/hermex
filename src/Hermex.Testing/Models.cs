namespace Hermex.Testing;

/// <summary>What to wait for. Every set field must match (case-insensitive "contains"); unset fields match anything.</summary>
public sealed record HermexMessageCriteria
{
    /// <summary>Text in the To header or the envelope recipients.</summary>
    public string? To { get; init; }

    /// <summary>Text in the From header or the envelope sender.</summary>
    public string? From { get; init; }

    /// <summary>Text in the subject.</summary>
    public string? Subject { get; init; }

    /// <summary>Text in a mailbox address the message was delivered to.</summary>
    public string? Mailbox { get; init; }

    /// <summary>
    /// Only messages stored after this cursor (from <see cref="HermexClient.MarkAsync"/>) match. When null, the
    /// newest message at the time of the call is the cursor, so take a mark before triggering the email if the
    /// app might send it before the wait starts.
    /// </summary>
    public long? After { get; init; }

    /// <inheritdoc />
    public override string ToString() =>
        string.Join(", ", new[] { ("to", To), ("from", From), ("subject", Subject), ("mailbox", Mailbox) }
            .Where(c => !string.IsNullOrEmpty(c.Item2)).Select(c => $"{c.Item1} contains \"{c.Item2}\""))
        is { Length: > 0 } text ? text : "any message";
}

/// <summary>A captured message, as listed by Hermex.</summary>
public sealed record HermexMessage
{
    /// <summary>The message id; ids only grow.</summary>
    public long Id { get; init; }

    /// <summary>When Hermex received it (UTC).</summary>
    public DateTimeOffset ReceivedAtUtc { get; init; }

    /// <summary>The sender's display name or address.</summary>
    public string From { get; init; } = string.Empty;

    /// <summary>The sender's address.</summary>
    public string FromAddress { get; init; } = string.Empty;

    /// <summary>The To header recipients.</summary>
    public string To { get; init; } = string.Empty;

    /// <summary>The subject.</summary>
    public string Subject { get; init; } = string.Empty;

    /// <summary>True when the message has an HTML body.</summary>
    public bool HasHtml { get; init; }

    /// <summary>Number of attachments.</summary>
    public int AttachmentCount { get; init; }

    /// <summary>Size of the raw message in bytes.</summary>
    public int RawSize { get; init; }

    /// <summary>True once read in the dashboard or through <see cref="HermexClient.GetMessageAsync"/>.</summary>
    public bool IsRead { get; init; }
}

/// <summary>A header field.</summary>
/// <param name="Name">Field name.</param>
/// <param name="Value">Unfolded value.</param>
public sealed record HermexHeader(string Name, string Value);

/// <summary>A captured message with its bodies and headers.</summary>
public sealed record HermexMessageDetail
{
    /// <summary>The message id.</summary>
    public long Id { get; init; }

    /// <summary>When Hermex received it (UTC).</summary>
    public DateTimeOffset ReceivedAtUtc { get; init; }

    /// <summary>The subject.</summary>
    public string Subject { get; init; } = string.Empty;

    /// <summary>The sender's display name.</summary>
    public string FromDisplay { get; init; } = string.Empty;

    /// <summary>The sender's address.</summary>
    public string FromAddress { get; init; } = string.Empty;

    /// <summary>The To header recipients.</summary>
    public string ToDisplay { get; init; } = string.Empty;

    /// <summary>The Cc header recipients.</summary>
    public string CcDisplay { get; init; } = string.Empty;

    /// <summary>The SMTP envelope sender (MAIL FROM).</summary>
    public string EnvelopeFrom { get; init; } = string.Empty;

    /// <summary>The SMTP envelope recipients (RCPT TO).</summary>
    public string EnvelopeTo { get; init; } = string.Empty;

    /// <summary>The plain-text body, or null.</summary>
    public string? TextBody { get; init; }

    /// <summary>The HTML body as sent (cid: links not rewritten), or null.</summary>
    public string? HtmlBody { get; init; }

    /// <summary>All header fields, in order.</summary>
    public IReadOnlyList<HermexHeader> Headers { get; init; } = Array.Empty<HermexHeader>();

    /// <summary>The first header with this name, or null.</summary>
    public string? Header(string name) =>
        Headers.FirstOrDefault(h => string.Equals(h.Name, name, StringComparison.OrdinalIgnoreCase))?.Value;
}

/// <summary>A unique link in a message's HTML body.</summary>
/// <param name="Href">The link target as written.</param>
/// <param name="Text">The visible link text.</param>
/// <param name="Scheme">The scheme (https, mailto, …) or "relative".</param>
/// <param name="Count">How many times the target appears.</param>
public sealed record HermexLink(string Href, string Text, string Scheme, int Count);
