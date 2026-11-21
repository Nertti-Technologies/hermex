using Hermex.Internal;
using Hermex.Diagnostics;
using Hermex.Mime;
using Hermex.Smtp;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace Hermex.Dashboard;

/// <summary>
/// <c>POST /messages/import</c> (ADR-0003 D14): stores uploaded <c>.eml</c> files through the same write queue as
/// SMTP, so search, waiters and webhooks see them, but they are never relayed automatically.
/// </summary>
internal static class EmlImport
{
    /// <summary>Files per request (ADR-0003 D14, estimate).</summary>
    public const int MaxFiles = 50;

    /// <summary>Whole request body (estimate: room for several full-size files while bounding the temporary files
    /// the form reader buffers to disk). Never below one file of <see cref="HermexOptions.MaxMessageSizeBytes"/>.</summary>
    private const long MaxRequestBytes = 256L * 1024 * 1024;

    /// <summary>Multipart framing allowance per request (estimate: boundaries and part headers).</summary>
    private const long FramingBytes = 1024 * 1024;


    /// <summary>The envelope sender recorded when an imported file has none; relay refuses it.</summary>
    internal const string UnknownSender = "unknown";

    /// <summary>Header bytes read when a file has no blank line (estimate: far beyond real header blocks).</summary>
    private const int MaxHeaderBytes = 256 * 1024;

    /// <summary>The session and endpoint recorded for imported messages.</summary>
    public const string Source = "import";

    public static async Task<IResult> HandleAsync(HttpRequest request, IReceivedMailSink sink, HermexOptions options,
        HermexEventDispatcher events, IHermexEventLog eventLog, CancellationToken ct)
    {
        if (!request.HasFormContentType)
            return Results.BadRequest(new { error = "Send the .eml files as multipart/form-data." });

        var maxFile = (long)options.MaxMessageSizeBytes;
        var maxBody = Math.Max(Math.Min(maxFile * MaxFiles, MaxRequestBytes), maxFile) + FramingBytes;
        // Only ever lower the server's limit: a host that set a smaller one keeps it.
        if (request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            maxBody = limit.MaxRequestBodySize is { } hostLimit ? Math.Min(hostLimit, maxBody) : maxBody;
            limit.MaxRequestBodySize = maxBody;
        }
        if (request.ContentLength > maxBody)
            return TooLarge();
        request.HttpContext.Features.Set<IFormFeature>(new FormFeature(request, new FormOptions
        {
            // Above the request limit, so an oversized chunked body hits the server's 413 first.
            MultipartBodyLengthLimit = maxBody + FramingBytes,
        }));

        IFormFileCollection files;
        try
        {
            files = (await request.ReadFormAsync(ct).ConfigureAwait(false)).Files;
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return TooLarge();
        }
        catch (Exception ex) when (ex is InvalidDataException or BadHttpRequestException)
        {
            // Malformed multipart, or a body without Content-Length that ran past the limit.
            return Results.BadRequest(new { error = "The upload is not a valid multipart form, or it is too large." });
        }

        if (files.Count == 0)
            return Results.BadRequest(new { error = "Choose at least one .eml file." });
        if (files.Count > MaxFiles)
            return Results.BadRequest(new { error = $"Import at most {MaxFiles} files at a time." });
        // Check every size first, so an oversized file never leaves a half-imported batch.
        if (files.Any(f => f.Length > maxFile))
            return Results.Json(new { error = $"Each file must be at most {options.MaxMessageSizeBytes} bytes." },
                statusCode: StatusCodes.Status413PayloadTooLarge);

        var importing = new HermexImportingContext
        {
            FileCount = files.Count(f => f.Length > 0),
            TotalBytes = files.Sum(f => f.Length),
            HttpContext = request.HttpContext,
            CancellationToken = ct,
        };
        if (!await events.ImportingAsync(importing).ConfigureAwait(false))
        {
            eventLog.Info("Storage", $"An import of {importing.FileCount} file(s) was refused by a host hook.");
            return Results.Json(new { error = importing.RejectionReason }, statusCode: StatusCodes.Status403Forbidden);
        }

        var imported = 0;
        var empty = 0;
        foreach (var file in files)
        {
            if (file.Length == 0)
            {
                empty++;
                continue;
            }
            var raw = new byte[file.Length];
            await using (var stream = file.OpenReadStream())
                await stream.ReadExactlyAsync(raw, ct).ConfigureAwait(false);

            switch (sink.Submit(Build(raw)))
            {
                case MailSubmissionResult.Accepted:
                    imported++;
                    break;
                case MailSubmissionResult.QueueFull:
                    Logged(eventLog, imported);
                    return Results.Json(new { imported, error = "The ingestion queue is full; import the rest shortly." },
                        statusCode: StatusCodes.Status503ServiceUnavailable);
                default:
                    Logged(eventLog, imported);
                    return Results.Json(new { imported, error = "Hermex is not accepting messages." },
                        statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        }
        Logged(eventLog, imported);
        return Results.Ok(new { imported, skipped = empty });
    }

    private static void Logged(IHermexEventLog eventLog, int imported)
    {
        if (imported > 0)
            eventLog.Info("Storage", $"Imported {imported} message(s) from .eml files.");
    }

    /// <summary>The bytes up to and including the blank line that ends the headers, within <see cref="MaxHeaderBytes"/>.</summary>
    private static ReadOnlySpan<byte> HeaderBlock(byte[] raw)
    {
        var span = raw.AsSpan(0, Math.Min(raw.Length, MaxHeaderBytes));
        var crlf = span.IndexOf("\r\n\r\n"u8);
        var lf = (crlf < 0 ? span : span[..crlf]).IndexOf("\n\n"u8); // only before the CRLF one
        return lf >= 0 ? span[..(lf + 2)] : crlf >= 0 ? span[..(crlf + 4)] : span;
    }

    private static IResult TooLarge() =>
        Results.Json(new { error = "The upload is too large." }, statusCode: StatusCodes.Status413PayloadTooLarge);

    /// <summary>
    /// The envelope comes from the headers: recipients from To, Cc and Bcc (plain addresses only, as an SMTP
    /// envelope would hold); the sender from Return-Path (<c>&lt;&gt;</c> stays the null sender), otherwise From.
    /// Only the header block is parsed here; the persistence path parses the whole message once.
    /// </summary>
    internal static ReceivedMessage Build(byte[] raw)
    {
        var mime = MimeParser.Parse(HeaderBlock(raw));
        var recipients = mime.To.Concat(mime.Cc).Concat(mime.Bcc)
            .Select(a => HermexRelayOptions.NormalizeAddress(a.Address))
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(SmtpSession.MaxRecipients)
            .ToArray();
        // "<>" is the null sender and stays "". With no usable sender at all the envelope says so, and relay
        // refuses it (not a plain address) instead of sending it as a bounce from <>.
        var returnPath = mime.Headers.Get("Return-Path")?.Trim();
        var sender = returnPath is null
            ? mime.From?.Address is { Length: > 0 } from ? from : UnknownSender
            : returnPath is "<>" ? string.Empty
            : MimeAddress.ParseSingle(returnPath)?.Address is { Length: > 0 } path ? path : UnknownSender;
        return new ReceivedMessage
        {
            SessionId = Source,
            MailFrom = sender,
            Recipients = recipients,
            RawData = raw,
            ReceivedAtUtc = DateTimeOffset.UtcNow,
            RemoteEndPoint = Source,
            Imported = true,
        };
    }
}
