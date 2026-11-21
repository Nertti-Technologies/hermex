namespace Hermex.Internal;

/// <summary>Fixed route paths used by the dashboard, API and SignalR hub.</summary>
internal static class HermexRoutes
{
    /// <summary>Root of the dashboard UI.</summary>
    public const string Base = "/hermex";

    /// <summary>Root of the JSON API consumed by the dashboard.</summary>
    public const string ApiBase = "/hermex/api";

    /// <summary>SignalR hub endpoint for live inbox updates.</summary>
    public const string Hub = "/hermex/hub";

    /// <summary>
    /// Header that state-changing API calls must carry when authorization policies are set.
    /// Sent by <c>hermex.js</c> and by the published Hermex.Testing client (<c>HermexClient</c>); renaming it
    /// breaks every released client version.
    /// </summary>
    public const string RequestHeader = "X-Hermex-Request";

    /// <summary>
    /// Response header of <c>GET /messages/wait</c> carrying the cursor the wait used (ADR-0003 D6). Read by the
    /// published Hermex.Testing client to continue a long wait; renaming it breaks every released client version.
    /// </summary>
    public const string CursorHeader = "X-Hermex-After";
}
