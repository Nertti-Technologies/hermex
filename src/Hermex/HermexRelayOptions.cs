namespace Hermex;

/// <summary>
/// Configures forwarding of captured messages to a real upstream SMTP server. Relay is off
/// by default — Hermex only captures unless this is explicitly enabled.
/// </summary>
public sealed class HermexRelayOptions
{
    /// <summary>Enables the relay feature (the dashboard "Relay" action and the relay API).</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// When <c>true</c>, every captured message is forwarded automatically as soon as it is
    /// stored. When <c>false</c>, messages are only relayed on demand from the dashboard.
    /// </summary>
    public bool AutomaticRelay { get; set; }

    /// <summary>Upstream SMTP host.</summary>
    public string Host { get; set; } = "localhost";

    /// <summary>Upstream SMTP port. Default <c>25</c>.</summary>
    public int Port { get; set; } = 25;

    /// <summary>Upgrade the upstream connection with <c>STARTTLS</c> when offered.</summary>
    public bool UseStartTls { get; set; }

    /// <summary>
    /// Use <c>STARTTLS</c>, require it, and validate the upstream server's certificate with the
    /// normal .NET checks (trusted chain, host name matches <see cref="Host"/>). Implies
    /// <see cref="UseStartTls"/>; a server that does not offer <c>STARTTLS</c> is refused, so a man in
    /// the middle cannot strip it and read the credentials. Default <c>false</c> (any certificate is accepted, as development relays
    /// often use self-signed ones); set it to <c>true</c> whenever the dashboard is shared.
    /// Code-only: the Settings page cannot change it.
    /// </summary>
    public bool ValidateCertificate { get; set; }

    /// <summary>Optional upstream AUTH user name.</summary>
    public string? Username { get; set; }

    /// <summary>Optional upstream AUTH password.</summary>
    public string? Password { get; set; }

    /// <summary>Connection / command timeout for the upstream conversation. Default 30 seconds.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Upstream hosts that relay may connect to: exact host names or IP addresses, compared
    /// case-insensitively, no wildcards. Empty (default) allows any host. Set this before exposing
    /// the dashboard to others, because the relay host can be changed from the Settings page.
    /// </summary>
    public IList<string> AllowedHosts { get; } = new List<string>();

    /// <summary>A copy of every value, for one relay attempt (<see cref="AllowedHosts"/> is shared, read-only use).</summary>
    internal HermexRelayOptions Snapshot() => (HermexRelayOptions)MemberwiseClone();

    /// <summary>Whether <paramref name="host"/> passes <see cref="AllowedHosts"/>.</summary>
    public bool IsHostAllowed(string? host)
    {
        if (AllowedHosts.Count == 0)
            return true;
        var candidate = host?.Trim();
        return !string.IsNullOrEmpty(candidate) &&
               AllowedHosts.Any(allowed => string.Equals(allowed?.Trim(), candidate, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Validates the relay configuration.</summary>
    public void Validate()
    {
        if (!Enabled)
            return;

        if (string.IsNullOrWhiteSpace(Host))
            throw new HermexConfigurationException("HermexRelayOptions.Host cannot be empty when relay is enabled.");
        if (!IsHostAllowed(Host))
            throw new HermexConfigurationException($"HermexRelayOptions.Host '{Host}' is not in HermexRelayOptions.AllowedHosts.");
        if (Port is < 1 or > 65535)
            throw new HermexConfigurationException($"HermexRelayOptions.Port must be between 1 and 65535 (was {Port}).");
        if (Timeout <= TimeSpan.Zero)
            throw new HermexConfigurationException("HermexRelayOptions.Timeout must be greater than zero.");
    }
}
