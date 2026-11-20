namespace Hermex;

/// <summary>The access tier an endpoint of the dashboard, API or hub belongs to.</summary>
public enum HermexEndpointTier
{
    /// <summary>Read mail, mark it read, the live hub and server status.</summary>
    Viewer = 0,
    /// <summary>Delete a message, clear the inbox and relay a message.</summary>
    Manager = 1,
    /// <summary>The Settings and Logs pages and their APIs.</summary>
    Admin = 2,
}

/// <summary>
/// Endpoint metadata that tags every Hermex endpoint with its <see cref="HermexEndpointTier"/>,
/// so a host can write its own policies or middleware against the tier instead of against URLs.
/// </summary>
/// <param name="Tier">The tier the endpoint belongs to.</param>
public sealed record HermexEndpointMetadata(HermexEndpointTier Tier);

/// <summary>
/// Puts the dashboard, API and hub behind the host's own ASP.NET Core authorization policies.
/// Hermex ships no identity of its own; sign-in, challenge and forbid behaviour belong to the host.
/// </summary>
public sealed class HermexAuthorizationOptions
{
    /// <summary>Policy for reading mail, marking it read, the hub and status. <c>null</c> = no requirement.</summary>
    public string? ViewerPolicy { get; set; }

    /// <summary>
    /// Policy for deleting messages, clearing the inbox and relaying a message.
    /// When <c>null</c>, the Viewer policy applies, so this tier is never weaker than the one below.
    /// </summary>
    public string? ManagerPolicy { get; set; }

    /// <summary>
    /// Policy for the Settings and Logs pages and their APIs.
    /// When <c>null</c>, the Manager tier's policy applies, so this tier is never weaker than the one below.
    /// </summary>
    public string? AdminPolicy { get; set; }

    /// <summary>
    /// Serve the embedded CSS, JavaScript and icons without a Hermex policy. Default <c>true</c>.
    /// When <c>false</c> they require the Viewer policy. A host <c>FallbackPolicy</c> still applies.
    /// </summary>
    public bool AllowAnonymousAssets { get; set; } = true;

    /// <summary>
    /// Extra browser origins (e.g. <c>https://mail.example.com</c>) allowed to open the live-update hub
    /// when policies are set. A page served from the same host is always allowed; add an origin
    /// only when a reverse proxy rewrites the <c>Host</c> header. Exact match, case-insensitive.
    /// </summary>
    public IList<string> AllowedHubOrigins { get; } = new List<string>();

    /// <summary>Whether any policy is set.</summary>
    public bool IsEnabled =>
        !string.IsNullOrWhiteSpace(ViewerPolicy) ||
        !string.IsNullOrWhiteSpace(ManagerPolicy) ||
        !string.IsNullOrWhiteSpace(AdminPolicy);

    /// <summary>
    /// The policy that protects <paramref name="tier"/>: its own policy, or else the effective
    /// policy of the tier below. <c>null</c> means the tier is open.
    /// </summary>
    public string? GetEffectivePolicy(HermexEndpointTier tier) => tier switch
    {
        HermexEndpointTier.Admin => Normalize(AdminPolicy) ?? GetEffectivePolicy(HermexEndpointTier.Manager),
        HermexEndpointTier.Manager => Normalize(ManagerPolicy) ?? GetEffectivePolicy(HermexEndpointTier.Viewer),
        _ => Normalize(ViewerPolicy),
    };

    private static string? Normalize(string? policy) =>
        string.IsNullOrWhiteSpace(policy) ? null : policy;
}
