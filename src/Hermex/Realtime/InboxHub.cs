using Hermex.Dashboard;
using Microsoft.AspNetCore.SignalR;

namespace Hermex.Realtime;

/// <summary>
/// SignalR hub that pushes live inbox updates to the dashboard. The dashboard is a pure
/// subscriber — all server-to-client events are raised through <see cref="InboxNotifier"/>.
/// </summary>
public sealed class InboxHub : Hub
{
    /// <summary>
    /// Group that receives live log entries. Only connections that pass the Admin tier join it,
    /// because logs are Admin-tier data; <see cref="InboxNotifier"/> sends log events here only.
    /// </summary>
    internal const string LogsGroup = "hermex-logs";

    private readonly HermexOptions _options;

    /// <summary>Creates the hub.</summary>
    public InboxHub(HermexOptions options) => _options = options;

    /// <inheritdoc />
    public override async Task OnConnectedAsync()
    {
        if (await HermexEndpointAuthorization.IsAuthorizedForTierAsync(
                Context.GetHttpContext(), _options.Authorization, HermexEndpointTier.Admin))
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, LogsGroup);
        }

        await base.OnConnectedAsync();
    }

    /// <summary>Client method names broadcast by the server.</summary>
    public static class Events
    {
        public const string MessageReceived = "messageReceived";
        public const string MessageUpdated = "messageUpdated";
        public const string MessageDeleted = "messageDeleted";
        public const string InboxCleared = "inboxCleared";
        public const string StatsChanged = "statsChanged";
        public const string LogAdded = "logAdded";
    }
}
