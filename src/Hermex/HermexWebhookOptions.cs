namespace Hermex;

/// <summary>Which networks webhook deliveries may connect to (ADR-0003 D4).</summary>
public enum HermexWebhookNetworkPolicy
{
    /// <summary>Loopback and private networks are allowed in the Development environment and blocked otherwise.</summary>
    Auto = 0,

    /// <summary>Loopback and private networks are always allowed (for example a receiver on the same machine).</summary>
    AllowPrivate = 1,

    /// <summary>Only public addresses are allowed, in every environment.</summary>
    PublicOnly = 2,
}

/// <summary>
/// Outbound webhooks: Hermex POSTs a signed JSON notice to each endpoint when a message is captured
/// (ADR-0003 D4). The notice carries metadata only (addresses, subject, size), never bodies or attachments.
/// Configured in code or configuration only: the dashboard cannot add or change endpoints. Keep secrets in
/// user-secrets, environment variables or a vault, never in committed files.
/// <para>
/// Delivery is at-least-once, unordered and in memory: notices still queued or retrying are lost on restart.
/// Link-local and cloud-metadata addresses (169.254.0.0/16, fe80::/10), multicast and unspecified addresses
/// are always refused; see <see cref="NetworkPolicy"/> for loopback and private networks.
/// </para>
/// </summary>
public sealed class HermexWebhookOptions
{
    /// <summary>Most endpoints allowed (estimate: a dev tool notifies a handful of receivers).</summary>
    public const int MaxEndpoints = 20;

    /// <summary>The receivers. Empty (the default) turns webhooks off.</summary>
    public IList<HermexWebhookEndpoint> Endpoints { get; } = new List<HermexWebhookEndpoint>();

    /// <summary>Time allowed for one delivery attempt. Default 10 s (GitHub's webhook timeout; ADR-0003 D4).</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Attempts per notice, 1–10. Default 5, at 0 s, 5 s, 30 s, 2 min and 10 min (±20 %), then every 10 min
    /// (estimate: a compressed form of the Standard Webhooks schedule, since dev receivers are short-lived).
    /// </summary>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>Notices waiting to be sent, per Hermex instance; the oldest is dropped when full. Default 1,000 (estimate).</summary>
    public int QueueCapacity { get; set; } = 1_000;

    /// <summary>Which networks deliveries may connect to. Default <see cref="HermexWebhookNetworkPolicy.Auto"/>.</summary>
    public HermexWebhookNetworkPolicy NetworkPolicy { get; set; } = HermexWebhookNetworkPolicy.Auto;

    /// <summary>Test hook: replaces the retry delays (attempt 2 onwards). Not part of the public contract.</summary>
    internal IReadOnlyList<TimeSpan>? RetryDelaysOverride { get; set; }

    // Validation bounds (estimates): a timeout over 2 min would hold a send slot too long; 10 attempts at the
    // 10-minute cap is already ~1.5 h of retries; 100,000 queued notices of at most ~20 kB is ~2 GB worst case
    // (queued plus as many in retry), far beyond a dev tool's needs, so it is the ceiling, not a target.
    internal void Validate()
    {
        if (Endpoints.Count > MaxEndpoints)
            throw new HermexConfigurationException($"HermexWebhookOptions.Endpoints allows at most {MaxEndpoints} endpoints.");
        if (Timeout < TimeSpan.FromSeconds(1) || Timeout > TimeSpan.FromMinutes(2))
            throw new HermexConfigurationException("HermexWebhookOptions.Timeout must be between 1 second and 2 minutes.");
        if (MaxAttempts is < 1 or > 10)
            throw new HermexConfigurationException("HermexWebhookOptions.MaxAttempts must be between 1 and 10.");
        if (QueueCapacity is < 1 or > 100_000)
            throw new HermexConfigurationException("HermexWebhookOptions.QueueCapacity must be between 1 and 100,000.");
        if (!Enum.IsDefined(NetworkPolicy))
            throw new HermexConfigurationException("HermexWebhookOptions.NetworkPolicy is not a known value.");
        for (var i = 0; i < Endpoints.Count; i++)
            (Endpoints[i] ?? throw new HermexConfigurationException($"HermexWebhookOptions.Endpoints[{i}] is null.")).Validate(i);
    }
}

/// <summary>One webhook receiver.</summary>
public sealed class HermexWebhookEndpoint
{
    /// <summary>The receiver's absolute <c>http</c> or <c>https</c> URL. No user name or password in the URL.</summary>
    public Uri? Url { get; set; }

    /// <summary>
    /// The signing secret in Standard Webhooks form: <c>whsec_</c> followed by base64 of 24–64 random bytes.
    /// Create one with <c>"whsec_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))</c>.
    /// </summary>
    public string Secret { get; set; } = string.Empty;

    /// <summary>
    /// Optional second secret during rotation: each notice is then signed with both, and the receiver accepts
    /// either (Standard Webhooks allows several signatures). Remove it once receivers use <see cref="Secret"/>.
    /// </summary>
    public string? PreviousSecret { get; set; }

    internal void Validate(int index)
    {
        var name = $"HermexWebhookOptions.Endpoints[{index}]";
        if (Url is null || !Url.IsAbsoluteUri || (Url.Scheme != Uri.UriSchemeHttps && Url.Scheme != Uri.UriSchemeHttp))
            throw new HermexConfigurationException($"{name}.Url must be an absolute http or https URL.");
        if (Url.UserInfo.Length > 0)
            throw new HermexConfigurationException($"{name}.Url must not contain a user name or password.");
        if (!Internal.WebhookSigner.TryDecodeSecret(Secret, out _))
            throw new HermexConfigurationException($"{name}.Secret must be \"whsec_\" followed by base64 of 24 to 64 bytes.");
        if (PreviousSecret is not null && !Internal.WebhookSigner.TryDecodeSecret(PreviousSecret, out _))
            throw new HermexConfigurationException($"{name}.PreviousSecret must be \"whsec_\" followed by base64 of 24 to 64 bytes.");
    }
}
