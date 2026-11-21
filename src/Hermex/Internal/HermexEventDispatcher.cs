using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Hermex.Internal;

/// <summary>
/// The single place that invokes <see cref="HermexEvents"/> hooks and enforces their failure
/// rules: veto hooks fail closed (an exception rejects the action), notification hooks fail open.
/// </summary>
internal sealed class HermexEventDispatcher
{
    private const string FailedHookReason = "Rejected because a host application hook failed.";

    private readonly HermexEvents _events;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<HermexEventDispatcher> _logger;

    public HermexEventDispatcher(HermexEvents events, IHttpContextAccessor httpContextAccessor,
        ILogger<HermexEventDispatcher> logger)
    {
        _events = events;
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
    }

    /// <summary>The current request, or <c>null</c> outside a request.</summary>
    public HttpContext? HttpContext => _httpContextAccessor.HttpContext;

    public Task<bool> SettingsChangingAsync(HermexSettingsChangingContext context) =>
        VetoAsync(context, _events.SettingsChanging, nameof(HermexEvents.OnSettingsChanging));

    public Task<bool> RelayingAsync(HermexRelayingContext context) =>
        VetoAsync(context, _events.Relaying, nameof(HermexEvents.OnRelaying));

    public Task<bool> DeletingAsync(HermexDeletingContext context) =>
        VetoAsync(context, _events.Deleting, nameof(HermexEvents.OnDeleting));

    public Task<bool> WebhookSendingAsync(HermexWebhookSendingContext context) =>
        VetoAsync(context, _events.WebhookSending, nameof(HermexEvents.OnWebhookSending));

    public Task<bool> FaultsChangingAsync(HermexFaultsChangingContext context) =>
        VetoAsync(context, _events.FaultsChanging, nameof(HermexEvents.OnFaultsChanging));

    public Task SettingsChangedAsync(HermexSettingsChangedContext context) =>
        NotifyAsync(context, _events.SettingsChanged, nameof(HermexEvents.OnSettingsChanged));

    public Task MessageStoredAsync(HermexMessageStoredContext context) =>
        NotifyAsync(context, _events.MessageStored, nameof(HermexEvents.OnMessageStored));

    /// <summary>Runs a veto hook; returns <c>true</c> when the action may proceed.</summary>
    private async Task<bool> VetoAsync<T>(T context, Func<T, Task> hook, string hookName)
        where T : HermexVetoContext
    {
        try
        {
            await hook(context).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Hermex: the {Hook} hook failed; the action is rejected.", hookName);
            context.Reject(FailedHookReason);
        }

        if (context.IsRejected)
            _logger.LogInformation("Hermex: the {Hook} hook rejected the action.", hookName);
        return !context.IsRejected;
    }

    private async Task NotifyAsync<T>(T context, Func<T, Task> hook, string hookName)
    {
        try
        {
            await hook(context).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Hermex: the {Hook} hook failed; ignored.", hookName);
        }
    }
}
