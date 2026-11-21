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

    /// <summary>Most patterns per recipient list (estimate: a test team lists a few domains or inboxes).</summary>
    public const int MaxRecipientPatterns = 200;

    /// <summary>
    /// Envelope recipients that relay may send to, as patterns: <c>*</c> matches any run of characters and <c>?</c>
    /// one character, ignoring case (<c>*@example.com</c>, <c>qa+*@corp.test</c>). Applies to manual and automatic
    /// relay alike; recipients that do not match are left out, and a message with none left is not relayed.
    /// <para>
    /// This list is the host's fixed limit: the dashboard cannot change it. The Settings page keeps its own lists,
    /// which can only narrow this one further (a recipient must pass both). Empty (the default) allows every
    /// recipient, as in 2.2, and Hermex logs a warning at start-up when relay is enabled with no allow-list at all;
    /// a later major version will treat an empty list as "relay to nobody" (ADR-0003 OD4).
    /// </para>
    /// Set it before the host starts.
    /// </summary>
    public IList<string> AllowedRecipients { get; } = new List<string>();

    /// <summary>
    /// Envelope recipients that relay never sends to, as patterns like <see cref="AllowedRecipients"/>. Fixed by the
    /// host; the Settings page can block more but never unblock these. A recipient that matches both lists is blocked.
    /// </summary>
    public IList<string> BlockedRecipients { get; } = new List<string>();

    // The dashboard layer (HermexSettings.RelayAllowedRecipients / RelayBlockedRecipients), swapped whole under the
    // lock by a settings change; relay takes one copy per attempt.
    private readonly object _recipientLock = new();
    private string[] _dashboardAllowed = [];
    private string[] _dashboardBlocked = [];

    /// <summary>Both layers of recipient rules, copied for one relay attempt.</summary>
    internal RecipientRuleSet RecipientRules()
    {
        lock (_recipientLock)
            return new RecipientRuleSet(Trimmed(AllowedRecipients), Trimmed(BlockedRecipients), _dashboardAllowed, _dashboardBlocked);
    }

    // Validate trims too, so a pattern read from configuration with stray spaces still matches.
    private static string[] Trimmed(IList<string> patterns) => patterns.Select(p => p.Trim()).ToArray();

    /// <summary>The dashboard layer only.</summary>
    internal (string[] Allowed, string[] Blocked) DashboardRecipientRules()
    {
        lock (_recipientLock)
            return (_dashboardAllowed, _dashboardBlocked);
    }

    /// <summary>Replaces the dashboard layer (a runtime settings change). The host's lists are never touched.</summary>
    internal void SetDashboardRecipientRules(IEnumerable<string> allowed, IEnumerable<string> blocked)
    {
        var a = allowed.Select(p => p.Trim()).ToArray();
        var b = blocked.Select(p => p.Trim()).ToArray();
        lock (_recipientLock)
        {
            _dashboardAllowed = a;
            _dashboardBlocked = b;
        }
    }

    /// <summary>
    /// A relayable form of <paramref name="address"/>, or null when it is not a plain <c>local@domain</c> address:
    /// printable ASCII only, exactly one <c>@</c>, no whitespace, quotes, angle brackets, commas, source route, or the
    /// <c>%</c> / <c>!</c> routing forms an upstream may follow to a different domain.
    /// A trailing dot on the domain is removed, so <c>a@x.test.</c> cannot slip past a rule for <c>*@x.test</c>.
    /// Anything else fails closed: the filter cannot vouch for what an upstream parser would make of it.
    /// </summary>
    internal static string? NormalizeAddress(string? address)
    {
        var value = address?.Trim();
        if (string.IsNullOrEmpty(value) || value.Length > Internal.AddressPattern.MaxLength)
            return null;
        foreach (var c in value)
        {
            // '%' and '!' are refused too: the percent hack and bang paths let an upstream that owns the domain
            // re-route a@x%real.com@ours.test to a@real.com (Postfix allow_percent_hack / swap_bangpath).
            if (c is <= ' ' or > '~' or '<' or '>' or ',' or '"' or ';' or ':' or '(' or ')' or '[' or ']' or '\\' or '%' or '!')
                return null;
        }
        var at = value.IndexOf('@');
        if (at <= 0 || at != value.LastIndexOf('@'))
            return null;
        var domain = value[(at + 1)..].TrimEnd('.');
        if (domain.Length == 0 || domain.StartsWith('.') || domain.Contains("..", StringComparison.Ordinal))
            return null;
        return value[..(at + 1)] + domain;
    }

    /// <summary>
    /// The recipients relay may send to, normalised: plain addresses that no block list matches and that every
    /// non-empty allow list (host and dashboard) matches.
    /// </summary>
    internal static List<string> FilterRecipients(IEnumerable<string> recipients, RecipientRuleSet rules)
    {
        static bool Any(string[] patterns, string address) => patterns.Any(p => Internal.AddressPattern.IsMatch(p, address));
        static bool Allows(string[] patterns, string address) => patterns.Length == 0 || Any(patterns, address);

        var kept = new List<string>();
        foreach (var recipient in recipients)
        {
            if (NormalizeAddress(recipient) is not { } address)
                continue;
            if (Any(rules.HostBlocked, address) || Any(rules.DashboardBlocked, address))
                continue;
            if (Allows(rules.HostAllowed, address) && Allows(rules.DashboardAllowed, address)
                && !kept.Contains(address, StringComparer.OrdinalIgnoreCase))
                kept.Add(address);
        }
        return kept;
    }

    /// <summary>Returns null when a recipient list is valid, otherwise what is wrong with it.</summary>
    internal static string? ValidateRecipientPatterns(IEnumerable<string?> patterns, string name)
    {
        var count = 0;
        foreach (var pattern in patterns)
        {
            count++;
            var trimmed = pattern?.Trim();
            if (string.IsNullOrEmpty(trimmed))
                return $"{name} contains an empty pattern.";
            if (trimmed.Length > Internal.AddressPattern.MaxLength)
                return $"{name} patterns must be at most {Internal.AddressPattern.MaxLength} characters.";
            if (trimmed.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c is ',' or '<' or '>'))
                return $"{name} patterns cannot contain spaces, commas, angle brackets or control characters.";
            if (trimmed.EndsWith('.'))
                return $"{name} patterns cannot end with a dot (addresses are compared without a trailing dot).";
        }
        return count > MaxRecipientPatterns ? $"{name} allows at most {MaxRecipientPatterns} patterns." : null;
    }

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
        var error = ValidateRecipientPatterns(AllowedRecipients, "HermexRelayOptions.AllowedRecipients")
                    ?? ValidateRecipientPatterns(BlockedRecipients, "HermexRelayOptions.BlockedRecipients");
        if (error is not null)
            throw new HermexConfigurationException(error);
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

/// <summary>Both layers of relay recipient rules: the host's fixed lists and the dashboard's narrowing lists.</summary>
internal sealed record RecipientRuleSet(string[] HostAllowed, string[] HostBlocked, string[] DashboardAllowed, string[] DashboardBlocked)
{
    /// <summary>True when neither layer restricts recipients (ADR-0003 OD4 warning).</summary>
    public bool AllowsAnyone => HostAllowed.Length == 0 && DashboardAllowed.Length == 0;
}
