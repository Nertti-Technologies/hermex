using Hermex.Diagnostics;
using Hermex.Background;
using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Text.RegularExpressions;
using Hermex.Analysis;
using Hermex.Configuration;
using Hermex.Internal;
using Hermex.Realtime;
using Hermex.Smtp;
using Hermex.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Hermex.Dashboard;

/// <summary>
/// Maps the JSON/file API consumed by the dashboard. These minimal-API routes live under
/// <c>/hermex/api</c> and never collide with the host application's own endpoints.
/// </summary>
internal static class HermexApiEndpoints
{
    public static void MapHermexApi(IEndpointRouteBuilder endpoints, HermexAuthorizationOptions authorization)
    {
        var api = endpoints.MapGroup(HermexRoutes.ApiBase);
        if (authorization.IsEnabled)
            api.AddEndpointFilter(HermexEndpointAuthorization.RequireRequestHeader);

        const HermexEndpointTier Viewer = HermexEndpointTier.Viewer;
        const HermexEndpointTier Manager = HermexEndpointTier.Manager;
        const HermexEndpointTier Admin = HermexEndpointTier.Admin;

        // ---- inbox listing -------------------------------------------------
        api.MapGet("/messages", async (
            int? page, int? pageSize, string? search, bool? unread, long? mailbox,
            IMailStore store, HermexOptions options) =>
        {
            // Validate at the boundary instead of letting the parser cut a long query mid-term.
            if (search?.Length > MessageSearch.MaxQueryLength)
                return Results.BadRequest(new { error = $"Search is limited to {MessageSearch.MaxQueryLength} characters." });

            var result = await store.GetMessagesAsync(new MessageQuery
            {
                Page = page ?? 1,
                PageSize = pageSize ?? options.DashboardPageSize,
                Search = search,
                UnreadOnly = unread ?? false,
                MailboxId = mailbox,
            });
            return Results.Json(result);
        }).WithHermexTier(Viewer, authorization);

        // ---- wait for a message (ADR-0003 D6) ------------------------------
        api.MapGet("/messages/wait", async (
            string? to, string? from, string? subject, string? mailbox, long? after, int? timeout,
            IMailStore store, MessageArrivals arrivals, HttpContext http) =>
        {
            var seconds = timeout ?? WaitRequest.DefaultTimeoutSeconds;
            if (seconds is < 1 or > WaitRequest.MaxTimeoutSeconds)
                return Results.BadRequest(new { error = $"timeout must be 1 to {WaitRequest.MaxTimeoutSeconds} seconds." });
            if (after < 0)
                return Results.BadRequest(new { error = "after must not be negative." });
            if (WaitRequest.BuildSearch(("to", to), ("from", from), ("subject", subject), ("mailbox", mailbox)) is not { } search)
                return Results.BadRequest(new { error = WaitRequest.InvalidValue });

            if (!arrivals.TryEnter())
                return Results.StatusCode(StatusCodes.Status429TooManyRequests);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted);
            try
            {
                deadline.CancelAfter(TimeSpan.FromSeconds(seconds));
                var ct = deadline.Token;

                // Default cursor: the newest id now, so mail stored before this request never matches.
                var cursor = after
                    ?? ((await store.GetMessagesAsync(new MessageQuery { PageSize = 1, SkipCount = true }, ct)).Items is [var newest, ..]
                        ? newest.Id : 0);
                var query = new MessageQuery { Search = search, AfterId = cursor, OldestFirst = true, PageSize = 1, SkipCount = true };
                http.Response.Headers[HermexRoutes.CursorHeader] = cursor.ToString(CultureInfo.InvariantCulture);

                while (true)
                {
                    var signal = arrivals.Next; // before the check, so nothing stored in between is missed
                    if ((await store.GetMessagesAsync(query, ct)).Items is [var match, ..])
                        return Results.Json(match);
                    await signal.WaitAsync(ct);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException || (ex is SqliteException && deadline.IsCancellationRequested))
            {
                // Timed out (or the client left, in which case nobody reads it). A deadline that interrupts a
                // running SQLite statement surfaces as SqliteException (SQLITE_INTERRUPT), not cancellation.
                return Results.NoContent();
            }
            finally
            {
                arrivals.Exit();
            }
        }).WithHermexTier(Viewer, authorization);

        // ---- mailboxes -----------------------------------------------------
        api.MapGet("/mailboxes", async (IMailStore store) =>
            Results.Json(await store.GetMailboxesAsync()))
            .WithHermexTier(Viewer, authorization);

        // ---- message detail (also marks the message read) ------------------
        api.MapGet("/messages/{id:long}", async (long id, IMailStore store, IInboxNotifier notifier) =>
        {
            var detail = await store.GetMessageAsync(id);
            if (detail is null)
                return Results.NotFound();

            if (!detail.IsRead)
            {
                await store.SetReadAsync(id, true);
                await notifier.MessageUpdatedAsync(id, true);
            }

            return Results.Json(detail);
        }).WithHermexTier(Viewer, authorization);

        // ---- rendered HTML body (cid: links rewritten to attachment URLs) --
        api.MapGet("/messages/{id:long}/html", async (long id, IMailStore store, HttpResponse response) =>
        {
            var detail = await store.GetMessageAsync(id);
            if (detail?.HtmlBody is null)
                return Results.NotFound();

            // The body is sender-controlled HTML served from the dashboard's origin. The dashboard
            // frames it with sandbox="", but opened directly in a tab its scripts would run as the
            // signed-in user. The CSP sandbox gives it an opaque origin with scripts off either way;
            // images and styles still load, so the preview looks the same.
            response.Headers.ContentSecurityPolicy = "sandbox";
            response.Headers.XContentTypeOptions = "nosniff";
            var html = RewriteCidLinks(id, detail);
            try
            {
                html = HtmlChecks.StripBaseAndRefresh(html);
            }
            catch (RegexMatchTimeoutException)
            {
                // Preview fidelity only (ADR-0003 D8); the CSP sandbox above still applies.
            }
            return Results.Content(html, "text/html; charset=utf-8");
        }).WithHermexTier(Viewer, authorization);

        // ---- HTML checks and authentication headers (ADR-0003 D5, D13); read-only, not stored ----
        api.MapGet("/messages/{id:long}/checks", async (long id, IMailStore store, CancellationToken ct) =>
        {
            var detail = await store.GetMessageAsync(id, ct);
            if (detail is null)
                return Results.NotFound();

            try
            {
                return Results.Json(new
                {
                    html = HtmlChecks.Run(detail.HtmlBody, detail.TextBody is not null, detail.Headers),
                    authentication = AuthenticationHeaders.Parse(detail.Headers),
                });
            }
            catch (RegexMatchTimeoutException)
            {
                return Results.Problem("The checks took too long on this message.", statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        }).WithHermexTier(Viewer, authorization);

        // ---- raw .eml download --------------------------------------------
        api.MapGet("/messages/{id:long}/raw", async (long id, IMailStore store) =>
        {
            var raw = await store.GetRawMessageAsync(id);
            return raw is null
                ? Results.NotFound()
                : Results.File(raw, "message/rfc822", $"hermex-message-{id}.eml");
        }).WithHermexTier(Viewer, authorization);

        // ---- attachment / inline resource download ------------------------
        api.MapGet("/messages/{id:long}/attachments/{index:int}", async (long id, int index, IMailStore store) =>
        {
            var attachment = await store.GetAttachmentAsync(id, index);
            if (attachment is null)
                return Results.NotFound();

            var contentType = string.IsNullOrWhiteSpace(attachment.Info.ContentType)
                ? "application/octet-stream"
                : attachment.Info.ContentType;
            var fileName = string.IsNullOrWhiteSpace(attachment.Info.FileName)
                ? $"attachment-{index}"
                : attachment.Info.FileName;

            return Results.File(attachment.Content, contentType, fileName);
        }).WithHermexTier(Viewer, authorization);

        // ---- mark read / unread -------------------------------------------
        api.MapPost("/messages/{id:long}/read", async (long id, bool? isRead, IMailStore store, IInboxNotifier notifier) =>
        {
            var target = isRead ?? true;
            if (!await store.SetReadAsync(id, target))
                return Results.NotFound();
            await notifier.MessageUpdatedAsync(id, target);
            return Results.Ok(new { id, isRead = target });
        }).WithHermexTier(Viewer, authorization);

        // ---- relay a message to the configured upstream SMTP server -------
        api.MapPost("/messages/{id:long}/relay", async (long id, IMailRelayService relay) =>
        {
            if (!relay.IsEnabled)
                return Results.BadRequest(new { error = "Relay is not enabled." });

            var result = await relay.RelayAsync(id);
            if (result.Success)
                return Results.Ok(new { id, relayed = true });
            return result.Rejected
                ? Forbidden(result.Error)
                : Results.Problem(result.Error ?? "Relay failed.");
        }).WithHermexTier(Manager, authorization);

        // ---- delete a single message --------------------------------------
        api.MapDelete("/messages/{id:long}", async (long id, IMailStore store, IInboxNotifier notifier,
            HermexEventDispatcher events) =>
        {
            var deleting = new HermexDeletingContext
            {
                MessageId = id,
                Scope = HermexDeleteScope.Message,
                HttpContext = events.HttpContext,
                CancellationToken = events.HttpContext?.RequestAborted ?? CancellationToken.None,
            };
            if (!await events.DeletingAsync(deleting))
                return Forbidden(deleting.RejectionReason);

            if (!await store.DeleteMessageAsync(id))
                return Results.NotFound();
            await notifier.MessageDeletedAsync(id);
            await notifier.StatsChangedAsync(await store.GetStatsAsync());
            return Results.Ok(new { id });
        }).WithHermexTier(Manager, authorization);

        // .eml import (ADR-0003 D14): through the SMTP write queue; never relayed automatically. The request
        // header is required even without authorization (with it, the api group already requires it): a
        // cross-site page can post a multipart form without a preflight, and this route stores what it is given.
        var import = api.MapPost("/messages/import", (HttpRequest request, IReceivedMailSink sink, HermexOptions options,
            HermexEventDispatcher events, IHermexEventLog eventLog, CancellationToken ct) =>
                EmlImport.HandleAsync(request, sink, options, events, eventLog, ct))
            .WithHermexTier(Manager, authorization);
        if (!authorization.IsEnabled)
            import.AddEndpointFilter(HermexEndpointAuthorization.RequireRequestHeader);

        // ---- bulk delete / mark read --------------------------------------
        // Separate routes so each carries one tier. An empty or oversized id list is a 400, never "all".
        api.MapPost("/messages/bulk-delete", async (BulkRequest? request, IMailStore store,
            IInboxNotifier notifier, HermexEventDispatcher events, CancellationToken ct) =>
        {
            if (BulkRequest.Validate(request) is { } invalid)
                return invalid;
            var ids = request!.Ids!;

            var deleting = new HermexDeletingContext
            {
                Scope = HermexDeleteScope.Messages,
                MessageIds = ids,
                HttpContext = events.HttpContext,
                CancellationToken = ct,
            };
            if (!await events.DeletingAsync(deleting))
                return Forbidden(deleting.RejectionReason);

            var deleted = await store.DeleteMessagesAsync(ids, ct);
            await notifier.MessagesChangedAsync(deleted, Array.Empty<long>(), null);
            await notifier.StatsChangedAsync(await store.GetStatsAsync(CancellationToken.None)); // committed: always broadcast
            return Results.Ok(new { deleted });
        }).WithHermexTier(Manager, authorization);

        api.MapPost("/messages/bulk-read", async (BulkRequest? request, IMailStore store,
            IInboxNotifier notifier, CancellationToken ct) =>
        {
            if (BulkRequest.Validate(request) is { } invalid)
                return invalid;

            var isRead = request!.IsRead ?? true;
            var updated = await store.SetReadAsync(request.Ids!, isRead, ct);
            await notifier.MessagesChangedAsync(Array.Empty<long>(), updated, isRead);
            await notifier.StatsChangedAsync(await store.GetStatsAsync(CancellationToken.None)); // committed: always broadcast
            return Results.Ok(new { updated, isRead });
        }).WithHermexTier(Viewer, authorization);

        // ---- clear the whole inbox ----------------------------------------
        api.MapDelete("/messages", async (IMailStore store, IInboxNotifier notifier, HermexEventDispatcher events) =>
        {
            var deleting = new HermexDeletingContext
            {
                MessageId = null,
                Scope = HermexDeleteScope.All,
                HttpContext = events.HttpContext,
                CancellationToken = events.HttpContext?.RequestAborted ?? CancellationToken.None,
            };
            if (!await events.DeletingAsync(deleting))
                return Forbidden(deleting.RejectionReason);

            var cleared = await store.ClearMessagesAsync();
            await notifier.InboxClearedAsync();
            await notifier.StatsChangedAsync(await store.GetStatsAsync());
            return Results.Ok(new { cleared });
        }).WithHermexTier(Manager, authorization);

        // ---- delete one mailbox (messages other mailboxes share are kept) -
        api.MapDelete("/mailboxes/{id:long}", async (long id, IMailStore store, IInboxNotifier notifier,
            HermexEventDispatcher events, CancellationToken ct) =>
        {
            var mailbox = await store.GetMailboxByIdAsync(id, ct);
            if (mailbox is null)
                return Results.NotFound();
            if (string.Equals(mailbox.Address, SqliteMailStore.InboxMailboxName, StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { error = "INBOX cannot be deleted. Use Delete all instead." });

            var deleting = new HermexDeletingContext
            {
                Scope = HermexDeleteScope.Mailbox,
                MailboxAddress = mailbox.Address,
                HttpContext = events.HttpContext,
                CancellationToken = ct,
            };
            if (!await events.DeletingAsync(deleting))
                return Forbidden(deleting.RejectionReason);

            MailboxDeleteResult? result;
            try
            {
                result = await store.DeleteMailboxAsync(id, ct);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (NotSupportedException ex)
            {
                return NotImplemented(ex.Message);
            }
            if (result is null)
                return Results.NotFound();
            await notifier.MailboxesChangedAsync(result.DeletedMessageIds);
            await notifier.StatsChangedAsync(await store.GetStatsAsync(CancellationToken.None)); // committed: always broadcast
            return Results.Ok(new { id, address = result.Address, messagesDeleted = result.DeletedMessageIds.Count });
        }).WithHermexTier(Manager, authorization);

        // ---- full reset: messages, mailboxes and logs (settings are kept) -
        api.MapPost("/reset", async (ResetRequest? request, IMailStore store, IInboxNotifier notifier,
            HermexEventDispatcher events, CancellationToken ct) =>
        {
            // Typed confirmation: a missing or unreadable body must never fall through to a reset.
            if (request?.Confirm != ResetRequest.ConfirmationText)
                return Results.BadRequest(new { error = $"Send {{\"confirm\":\"{ResetRequest.ConfirmationText}\"}} to reset." });

            var deleting = new HermexDeletingContext
            {
                Scope = HermexDeleteScope.Reset,
                HttpContext = events.HttpContext,
                CancellationToken = ct,
            };
            if (!await events.DeletingAsync(deleting))
                return Forbidden(deleting.RejectionReason);

            StoreResetResult result;
            try
            {
                result = await store.ResetAsync(ct);
            }
            catch (NotSupportedException ex)
            {
                return NotImplemented(ex.Message);
            }
            await notifier.InboxClearedAsync();
            await notifier.MailboxesChangedAsync(Array.Empty<long>());
            await notifier.StatsChangedAsync(await store.GetStatsAsync(CancellationToken.None)); // committed: always broadcast
            return Results.Ok(new
            {
                messagesDeleted = result.MessagesDeleted,
                mailboxesDeleted = result.MailboxesDeleted,
                logsDeleted = result.LogsDeleted,
            });
        }).WithHermexTier(Admin, authorization);

        // ---- server status, statistics and effective options --------------
        api.MapGet("/status", async (HttpContext http, IMailStore store, HermexRuntimeState state,
            HermexOptions options) =>
        {
            var stats = await store.GetStatsAsync();

            // Server paths and raw error text are Admin-tier detail; Viewers get null.
            var showServerDetail =
                await HermexEndpointAuthorization.IsAuthorizedForTierAsync(http, authorization, Admin);

            return Results.Json(new
            {
                server = new
                {
                    status = state.Status.ToString(),
                    configuredPort = state.ConfiguredPort,
                    listeningPort = state.ListeningPort,
                    listenAddress = state.ListenAddress,
                    host = state.ServerHostName,
                    imapEnabled = state.ImapEnabled,
                    imapStatus = state.ImapStatus.ToString(),
                    imapListeningPort = state.ImapListeningPort,
                    startedAtUtc = state.StartedAtUtc,
                    uptimeSeconds = (long)state.Uptime.TotalSeconds,
                    databasePath = showServerDetail ? state.DatabasePath : null,
                    lastError = showServerDetail ? state.LastError : null,
                    acceptedConnections = state.AcceptedConnections,
                    activeConnections = state.ActiveConnections,
                    messagesReceived = state.MessagesReceived,
                    messagesRejected = state.MessagesRejected,
                    bytesReceived = state.BytesReceived,
                },
                stats,
                options = new
                {
                    maxMessageSizeBytes = options.MaxMessageSizeBytes,
                    maxConcurrentConnections = options.MaxConcurrentConnections,
                    retentionMaxMessages = options.RetentionMaxMessages,
                    requireAuthentication = options.RequireAuthentication,
                    tlsMode = options.TlsMode.ToString(),
                    relayEnabled = options.Relay.Enabled,
                    enableImap = options.EnableImap,
                    imapPort = options.ImapPort,
                    allowRuntimeSettings = options.AllowRuntimeSettings,
                },
            });
        }).WithHermexTier(Viewer, authorization);

        // ---- webhook deliveries (ADR-0003 D4); endpoints themselves are code-only ----
        api.MapGet("/webhooks/deliveries", (WebhookService webhooks) => Results.Json(new
        {
            enabled = webhooks.Enabled,
            dropped = webhooks.Dropped,
            skipped = webhooks.Skipped,
            endpoints = webhooks.Endpoints,
            deliveries = webhooks.Deliveries,
        })).WithHermexTier(Admin, authorization);

        api.MapPost("/webhooks/{index:int}/resume", (int index, WebhookService webhooks) =>
            webhooks.Resume(index) ? Results.Ok() : Results.NotFound()).WithHermexTier(Admin, authorization);

        // ---- diagnostic logs ----------------------------------------------
        api.MapGet("/logs", async (int? limit, IMailStore store) =>
        {
            var logs = await store.GetLogsAsync(limit ?? 200);
            return Results.Json(logs);
        }).WithHermexTier(Admin, authorization);

        api.MapDelete("/logs", async (IMailStore store) =>
        {
            var cleared = await store.ClearLogsAsync();
            return Results.Ok(new { cleared });
        }).WithHermexTier(Admin, authorization);

        // ---- SMTP fault injection (ADR-0003 D11); in memory only ------------
        api.MapGet("/faults", (FaultInjector faults) => Results.Json(faults.Current))
            .WithHermexTier(Admin, authorization);

        api.MapPut("/faults", async (FaultsUpdate update, FaultInjector faults, HermexEventDispatcher events,
            CancellationToken ct) =>
        {
            switch (faults.Validate(update.Enabled, update.Rules))
            {
                case { Forbidden: true } refused: return Forbidden(refused.Error);
                case { } invalid: return Results.BadRequest(new { error = invalid.Error });
            }
            var changing = new HermexFaultsChangingContext
            {
                Enabled = update.Enabled,
                Rules = FaultInjector.CopyAll(update.Rules),
                HttpContext = events.HttpContext,
                CancellationToken = ct,
            };
            if (!await events.FaultsChangingAsync(changing))
                return Forbidden(changing.RejectionReason);
            faults.Apply(update.Enabled, update.Rules);
            return Results.Ok(faults.Current);
        }).WithHermexTier(Admin, authorization);

        // ---- runtime-editable settings ------------------------------------
        api.MapGet("/settings", (HermexSettingsService settings) =>
            Results.Json(settings.GetCurrent()))
            .WithHermexTier(Admin, authorization);

        api.MapPut("/settings", async (HermexSettings update, HermexSettingsService settings) =>
        {
            var result = await settings.UpdateAsync(update, HermexSettingsChangeSource.Dashboard);
            return result.Status switch
            {
                HermexSettingsUpdateStatus.Saved =>
                    Results.Ok(new { saved = true, listenerRestarted = result.ListenerRestarted }),
                HermexSettingsUpdateStatus.Invalid => Results.BadRequest(new { error = result.Error }),
                _ => Forbidden(result.Error),
            };
        }).WithHermexTier(Admin, authorization);
    }

    /// <summary>Body of <c>PUT /faults</c>: the switch and the full rule list, replacing the current ones.</summary>
    internal sealed record FaultsUpdate(bool Enabled, List<HermexFaultRule>? Rules);

    /// <summary>Body of <c>POST /reset</c>.</summary>
    internal sealed record ResetRequest(string? Confirm)
    {
        /// <summary>
        /// The word the caller must send. Pages/Settings.cshtml renders it into the page (data-confirm-word)
        /// and settings.js sends what the page says, so this is the only place it is defined.
        /// </summary>
        public const string ConfirmationText = "RESET";
    }

    /// <summary>Body of the bulk routes.</summary>
    internal sealed record BulkRequest(IReadOnlyList<long>? Ids, bool? IsRead)
    {
        /// <summary>Upper bound per request: the largest dashboard page (MessageQuery's page-size cap is 500).</summary>
        public const int MaxIds = 500;

        public static IResult? Validate(BulkRequest? request)
        {
            if (request?.Ids is not { Count: > 0 } ids)
                return Results.BadRequest(new { error = "ids must list at least one message." });
            if (ids.Count > MaxIds)
                return Results.BadRequest(new { error = $"At most {MaxIds} ids per request." });
            return null;
        }
    }

    /// <summary>501 with a JSON reason, for a host-supplied store that lacks a 2.3.0 operation.</summary>
    private static IResult NotImplemented(string reason) =>
        Results.Json(new { error = reason }, statusCode: StatusCodes.Status501NotImplemented);

    /// <summary>403 with a JSON reason, for actions the host locked or a hook rejected.</summary>
    private static IResult Forbidden(string? reason) =>
        Results.Json(new { error = reason ?? "Forbidden by the host application." },
            statusCode: StatusCodes.Status403Forbidden);

    /// <summary>Rewrites <c>cid:</c> references in an HTML body to inline-attachment URLs.</summary>

    /// <summary>Request rules for <c>GET /messages/wait</c> (ADR-0003 D6).</summary>
    internal static class WaitRequest
    {
        /// <summary>Default wait (ADR-0003 D6).</summary>
        public const int DefaultTimeoutSeconds = 30;

        /// <summary>Longest wait per request (estimate, from a vendor's documented 120,000 ms maximum).
        /// The Hermex.Testing client splits longer waits into several requests.</summary>
        public const int MaxTimeoutSeconds = 120;

        /// <summary>Longest criterion value (estimate: longer than any address or subject a test matches on).</summary>
        public const int MaxValueLength = 200;

        public const string InvalidValue =
            "to, from, subject and mailbox are limited to 200 characters each (and the search they form to 512), " +
            "and must not contain quotes or control characters.";

        /// <summary>Turns the criteria into the search syntax (ADR-0003 D9), or null when a value is invalid.</summary>
        public static string? BuildSearch(params (string Operator, string? Value)[] criteria)
        {
            var terms = new List<string>();
            foreach (var (op, value) in criteria)
            {
                if (string.IsNullOrEmpty(value))
                    continue;
                if (value.Length > MaxValueLength || value.Any(c => c == '"' || char.IsControl(c)))
                    return null;
                terms.Add($"{op}:\"{value}\"");
            }
            var search = string.Join(' ', terms);
            // The search parser ignores text past its limit; refuse rather than match on fewer criteria.
            return search.Length > MessageSearch.MaxQueryLength ? null : search;
        }
    }

    private static string RewriteCidLinks(long messageId, MessageDetail detail)
    {
        var html = detail.HtmlBody ?? string.Empty;
        if (html.Length == 0)
            return html;

        for (var i = 0; i < detail.Attachments.Count; i++)
        {
            var contentId = detail.Attachments[i].ContentId;
            if (string.IsNullOrEmpty(contentId))
                continue;

            var url = $"{HermexRoutes.ApiBase}/messages/{messageId}/attachments/{i}";
            html = html.Replace("cid:" + contentId, url, StringComparison.OrdinalIgnoreCase);
        }

        return html;
    }
}
