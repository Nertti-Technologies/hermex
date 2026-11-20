using Microsoft.AspNetCore.Http;

namespace Hermex;

/// <summary>Where a settings change came from.</summary>
public enum HermexSettingsChangeSource
{
    /// <summary>Saved from the dashboard Settings page (<c>PUT /hermex/api/settings</c>).</summary>
    Dashboard = 0,
    /// <summary>Stored in the database by an earlier run and applied at startup.</summary>
    Persisted = 1,
    /// <summary>Applied by host code through <see cref="IHermexSettings"/>.</summary>
    Api = 2,
}

/// <summary>
/// Hooks around Hermex's sensitive actions, in the style of ASP.NET Core's
/// <c>CookieAuthenticationEvents</c>. Assign the <c>On…</c> delegates, or subclass this type,
/// override the virtual methods and register it with
/// <c>services.AddSingleton&lt;HermexEvents, MyEvents&gt;()</c> (before or after <c>AddMail4Dev</c>);
/// a registered subclass replaces <see cref="HermexOptions.Events"/>.
/// <para>
/// Veto hooks run <em>before</em> the action and fail closed: an exception is logged and treated
/// as a rejection. Notification hooks run <em>after</em> the action and fail open: an exception
/// is logged and ignored. Hooks run from Hermex's own services, so they also cover automatic
/// relay and startup; <c>HttpContext</c> is <c>null</c> outside a request.
/// </para>
/// </summary>
public class HermexEvents
{
    /// <summary>Veto hook: before settings are applied. May change <see cref="HermexSettingsChangingContext.Proposed"/>.</summary>
    public Func<HermexSettingsChangingContext, Task> OnSettingsChanging { get; set; } = _ => Task.CompletedTask;

    /// <summary>Veto hook: before a message is relayed upstream, on demand or automatically.</summary>
    public Func<HermexRelayingContext, Task> OnRelaying { get; set; } = _ => Task.CompletedTask;

    /// <summary>
    /// Veto hook: before a message is deleted or the inbox is cleared from the dashboard or API.
    /// IMAP <c>EXPUNGE</c> only removes a message from that IMAP folder (the stored message and the
    /// dashboard inbox keep it), and retention pruning is the host's own configuration, so neither
    /// runs this hook.
    /// </summary>
    public Func<HermexDeletingContext, Task> OnDeleting { get; set; } = _ => Task.CompletedTask;

    /// <summary>Notification hook: after settings were applied.</summary>
    public Func<HermexSettingsChangedContext, Task> OnSettingsChanged { get; set; } = _ => Task.CompletedTask;

    /// <summary>
    /// Notification hook: after a message was stored. Delivered from a bounded background queue
    /// (capacity <see cref="HermexOptions.WriteQueueCapacity"/>) so it never slows mail capture;
    /// when a slow hook lets the queue fill, further events are dropped and counted in a warning.
    /// </summary>
    public Func<HermexMessageStoredContext, Task> OnMessageStored { get; set; } = _ => Task.CompletedTask;

    /// <summary>Invokes <see cref="OnSettingsChanging"/>.</summary>
    public virtual Task SettingsChanging(HermexSettingsChangingContext context) => OnSettingsChanging(context);

    /// <summary>Invokes <see cref="OnRelaying"/>.</summary>
    public virtual Task Relaying(HermexRelayingContext context) => OnRelaying(context);

    /// <summary>Invokes <see cref="OnDeleting"/>.</summary>
    public virtual Task Deleting(HermexDeletingContext context) => OnDeleting(context);

    /// <summary>Invokes <see cref="OnSettingsChanged"/>.</summary>
    public virtual Task SettingsChanged(HermexSettingsChangedContext context) => OnSettingsChanged(context);

    /// <summary>Invokes <see cref="OnMessageStored"/>.</summary>
    public virtual Task MessageStored(HermexMessageStoredContext context) => OnMessageStored(context);
}

/// <summary>Base for the contexts of veto hooks: call <see cref="Reject"/> to stop the action.</summary>
public abstract class HermexVetoContext
{
    /// <summary>The current request, or <c>null</c> when the action runs outside a request.</summary>
    public HttpContext? HttpContext { get; init; }

    /// <summary>Cancelled when the request is aborted or the operation is cancelled.</summary>
    public CancellationToken CancellationToken { get; init; }

    /// <summary>Whether a hook rejected the action.</summary>
    public bool IsRejected { get; private set; }

    /// <summary>The reason given by the hook that rejected the action.</summary>
    public string? RejectionReason { get; private set; }

    /// <summary>Stops the action. The reason is returned to the caller, so keep personal data out of it.</summary>
    public void Reject(string reason)
    {
        IsRejected = true;
        RejectionReason = string.IsNullOrWhiteSpace(reason) ? "Rejected by the host application." : reason;
    }
}

/// <summary>Context for <see cref="HermexEvents.OnSettingsChanging"/>.</summary>
public sealed class HermexSettingsChangingContext : HermexVetoContext
{
    /// <summary>The effective settings before the change (a copy).</summary>
    public required HermexSettings Current { get; init; }

    /// <summary>The settings about to be applied. A mutable copy: changes are validated again and then applied.</summary>
    public required HermexSettings Proposed { get; init; }

    /// <summary>Where the change came from.</summary>
    public required HermexSettingsChangeSource Source { get; init; }
}

/// <summary>Context for <see cref="HermexEvents.OnRelaying"/>.</summary>
public sealed class HermexRelayingContext : HermexVetoContext
{
    /// <summary>The stored message being relayed.</summary>
    public required long MessageId { get; init; }

    /// <summary>The upstream SMTP host.</summary>
    public required string Host { get; init; }

    /// <summary>The upstream SMTP port.</summary>
    public required int Port { get; init; }

    /// <summary>The envelope sender.</summary>
    public required string From { get; init; }

    /// <summary>The envelope recipients.</summary>
    public required IReadOnlyList<string> Recipients { get; init; }

    /// <summary><c>true</c> for automatic relay of newly captured mail, <c>false</c> for an on-demand relay.</summary>
    public required bool Automatic { get; init; }
}

/// <summary>Context for <see cref="HermexEvents.OnDeleting"/>.</summary>
public sealed class HermexDeletingContext : HermexVetoContext
{
    /// <summary>The message being deleted, or <c>null</c> when the whole inbox is being cleared.</summary>
    public long? MessageId { get; init; }
}

/// <summary>Context for <see cref="HermexEvents.OnSettingsChanged"/>.</summary>
public sealed class HermexSettingsChangedContext
{
    /// <summary>The effective settings before the change.</summary>
    public required HermexSettings Previous { get; init; }

    /// <summary>The effective settings after the change.</summary>
    public required HermexSettings Current { get; init; }

    /// <summary>Where the change came from.</summary>
    public required HermexSettingsChangeSource Source { get; init; }

    /// <summary>Whether the SMTP or IMAP listener is re-binding because of the change.</summary>
    public required bool ListenerRestarted { get; init; }

    /// <summary>The current request, or <c>null</c> when the change happened outside a request.</summary>
    public HttpContext? HttpContext { get; init; }

    /// <summary>Cancelled when the request is aborted or the operation is cancelled.</summary>
    public CancellationToken CancellationToken { get; init; }
}

/// <summary>Context for <see cref="HermexEvents.OnMessageStored"/>.</summary>
public sealed class HermexMessageStoredContext
{
    /// <summary>The id of the stored message.</summary>
    public required long MessageId { get; init; }

    /// <summary>The sender address.</summary>
    public required string From { get; init; }

    /// <summary>The envelope recipients.</summary>
    public required IReadOnlyList<string> To { get; init; }

    /// <summary>The subject.</summary>
    public required string Subject { get; init; }

    /// <summary>The raw message size in bytes.</summary>
    public required long SizeBytes { get; init; }

    /// <summary>Cancelled when the host is shutting down.</summary>
    public CancellationToken CancellationToken { get; init; }
}
