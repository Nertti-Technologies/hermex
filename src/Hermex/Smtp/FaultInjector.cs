using System.Globalization;
using System.Text;
using Hermex.Diagnostics;
using Hermex.Internal;
using Microsoft.Extensions.Hosting;

namespace Hermex.Smtp;

/// <summary>A fault chosen for one SMTP step: wait, then drop the connection or send a failure reply.</summary>
internal sealed record FaultAction(string Source, HermexSmtpStage Stage, TimeSpan? Delay, bool Drop, int? ReplyCode)
{
    /// <summary>The reply line for <see cref="ReplyCode"/>, with RFC 5321 §4.2.2/§4.2.3 wording and an RFC 3463 status.</summary>
    public string Reply => ReplyCode switch
    {
        421 => "421 4.3.2 Service not available, closing transmission channel (injected fault)",
        450 => "450 4.2.0 Requested mail action not taken: mailbox unavailable (injected fault)",
        451 => "451 4.3.0 Requested action aborted: local error in processing (injected fault)",
        452 => "452 4.3.1 Requested action not taken: insufficient system storage (injected fault)",
        550 => "550 5.1.1 Requested action not taken: mailbox unavailable (injected fault)",
        552 => "552 5.3.4 Requested mail action aborted: exceeded storage allocation (injected fault)",
        553 => "553 5.1.3 Requested action not taken: mailbox name not allowed (injected fault)",
        554 => "554 5.3.0 Transaction failed (injected fault)",
        { } code => $"{code} {code / 100}.0.0 Injected fault",
        null => string.Empty,
    };

    /// <summary>True when the session must end after this fault: a drop, or 421 (RFC 5321 §3.8).</summary>
    public bool ClosesConnection => Drop || ReplyCode == 421;

    /// <summary>For the diagnostic log: source, stage and effect. Never an address.</summary>
    public string Describe()
    {
        var effect = Drop ? "connection dropped" : ReplyCode is { } code ? "reply " + code.ToString(CultureInfo.InvariantCulture) : "no change";
        return Delay is { } d
            ? $"Fault injected ({Source}) at {Stage}: delay {d.TotalMilliseconds:0} ms, {effect}."
            : $"Fault injected ({Source}) at {Stage}: {effect}.";
    }
}

/// <summary>The current fault state, as shown and edited on the Settings page.</summary>
internal sealed record FaultState(bool Enabled, bool AllowedHere, bool RuntimeEditable, bool AllowHeaderTrigger,
    IReadOnlyList<HermexFaultRule> Rules);

/// <summary>
/// Decides which fault, if any, applies at each SMTP step (ADR-0003 D11). Rules and the on/off switch can be
/// replaced at runtime from the dashboard; the change lives in memory only. Thread-safe: sessions read an
/// immutable snapshot, and updates swap it.
/// </summary>
internal sealed class FaultInjector
{
    /// <summary>Header bytes scanned for <c>X-Hermex-Fault</c> (estimate: well past any real header block).</summary>
    private const int MaxHeaderScan = 64 * 1024;

    private const string HeaderName = "X-Hermex-Fault"; // FindHeader matches the same name as a UTF-8 literal

    private readonly HermexOptions _options;
    private readonly IHermexEventLog _eventLog;
    private readonly bool _allowedHere;
    private readonly Random _random;
    private readonly object _randomLock = new();
    private volatile Snapshot _snapshot;

    public FaultInjector(HermexOptions options, IHostEnvironment environment, IHermexEventLog eventLog)
    {
        _options = options;
        _eventLog = eventLog;
        var faults = options.Faults;
        _allowedHere = environment.IsDevelopment() || faults.AllowOutsideDevelopment;
        if (faults.Enabled && !_allowedHere)
            throw new HermexConfigurationException(
                "HermexOptions.Faults.Enabled is set outside the Development environment. Fault injection fails every " +
                "client's mail on a shared server; set Faults.AllowOutsideDevelopment to allow it here.");
        _random = faults.RandomSeed is { } seed ? new Random(seed) : new Random();
        _snapshot = new Snapshot(faults.Enabled, faults.Rules.Select(Copy).ToArray());
    }

    public bool Enabled => _snapshot.Enabled;

    public FaultState Current
    {
        get
        {
            var s = _snapshot;
            return new FaultState(s.Enabled, _allowedHere, _options.AllowRuntimeSettings,
                _options.Faults.AllowHeaderTrigger, s.Rules.Select(Copy).ToArray());
        }
    }

    /// <summary>Checks a replacement switch and rules. Returns null when allowed, or why not (and whether forbidden).</summary>
    public (string Error, bool Forbidden)? Validate(bool enabled, IReadOnlyList<HermexFaultRule>? rules)
    {
        if (!_options.AllowRuntimeSettings)
            return ("The host does not allow settings to be changed at runtime.", true);
        if (enabled && !_allowedHere)
            return ("Fault injection is allowed only in the Development environment unless the host sets Faults.AllowOutsideDevelopment.", true);

        rules ??= Array.Empty<HermexFaultRule>();
        if (rules.Count > HermexFaultOptions.MaxRules)
            return ($"At most {HermexFaultOptions.MaxRules} rules are allowed.", false);
        for (var i = 0; i < rules.Count; i++)
        {
            var error = rules[i] is { } rule ? rule.Validate(_options.SessionTimeout) : "is null";
            if (error is not null)
                return ($"Rule {i + 1} {error}", false);
        }
        return null;
    }

    /// <summary>Replaces the switch and rules; call <see cref="Validate"/> (and the veto hook) first.</summary>
    public void Apply(bool enabled, IReadOnlyList<HermexFaultRule>? rules)
    {
        rules ??= Array.Empty<HermexFaultRule>();
        _snapshot = new Snapshot(enabled, rules.Select(Copy).ToArray());
        _eventLog.Info("Faults", $"Fault injection {(enabled ? "enabled" : "disabled")} from the dashboard with {rules.Count} rule(s).");
    }

    /// <summary>Copies of rules, for hooks and responses.</summary>
    public static HermexFaultRule[] CopyAll(IEnumerable<HermexFaultRule>? rules) => (rules ?? []).Select(Copy).ToArray();

    /// <summary>The first rule that fires at this step, or null. <paramref name="address"/> is the sender or recipient.</summary>
    public FaultAction? Check(HermexSmtpStage stage, string? address)
    {
        var s = _snapshot;
        if (!s.Enabled)
            return null;
        for (var i = 0; i < s.Rules.Length; i++)
        {
            var rule = s.Rules[i];
            if (rule.Stage != stage || (rule.Match is not null && !AddressPattern.IsMatch(rule.Match, address ?? string.Empty)))
                continue;
            if (!Fires(rule, s.Counters, i))
                continue;
            return new FaultAction($"rule {i + 1}", stage, rule.Delay, rule.Drop, rule.ReplyCode);
        }
        return null;
    }

    /// <summary>
    /// The fault a message asks for with <c>X-Hermex-Fault</c>, or null. Invalid values are ignored with a warning,
    /// so a typo never silently becomes a different fault.
    /// </summary>
    public FaultAction? FromHeader(ReadOnlySpan<byte> raw, string sessionId)
    {
        if (!_snapshot.Enabled || !_options.Faults.AllowHeaderTrigger)
            return null;
        var value = FindHeader(raw[..Math.Min(raw.Length, MaxHeaderScan)]);
        if (value is null)
            return null;

        int? code = null;
        TimeSpan? delay = null;
        var drop = false;
        var parts = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return Ignore();
        foreach (var part in parts)
        {
            if (part.Equals("drop", StringComparison.OrdinalIgnoreCase) && !drop)
                drop = true;
            else if (part.Length == 3 && int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var c)
                     && c is >= 400 and <= 599 && code is null)
                code = c;
            else if (part.StartsWith("delay=", StringComparison.OrdinalIgnoreCase) && delay is null
                     && TryParseDuration(part[6..], out var d) && d <= HermexFaultRule.MaxDelayFor(_options.SessionTimeout))
                delay = d;
            else
                return Ignore();
        }
        if (drop && code is not null)
            return Ignore();
        return new FaultAction(HeaderName + " header", HermexSmtpStage.Data, delay, drop, code);

        FaultAction? Ignore()
        {
            _eventLog.Warning("Faults", $"Ignored an invalid {HeaderName} header; expected a 4xx/5xx code, drop or delay=…, comma-separated.", sessionId);
            return null;
        }
    }

    private bool Fires(HermexFaultRule rule, long[] counters, int index)
    {
        if (rule.EveryNth is { } n)
            return Interlocked.Increment(ref counters[index]) % n == 0;
        if (rule.Probability >= 100)
            return true;
        if (rule.Probability <= 0)
            return false;
        lock (_randomLock)
            return _random.Next(100) < rule.Probability;
    }

    /// <summary>
    /// The unfolded value of the first X-Hermex-Fault header in the header block (RFC 5322 §2.2.3: continuation
    /// lines start with a space or tab), or null.
    /// </summary>
    private static string? FindHeader(ReadOnlySpan<byte> raw)
    {
        var name = "X-Hermex-Fault:"u8;
        StringBuilder? value = null;
        var lineStart = 0;
        while (lineStart < raw.Length)
        {
            var end = raw[lineStart..].IndexOf((byte)'\n');
            var line = end < 0 ? raw[lineStart..] : raw.Slice(lineStart, end);
            if (line.Length > 0 && line[^1] == '\r')
                line = line[..^1];
            if (line.Length == 0)
                break; // end of the header block
            if (value is not null)
            {
                if (line[0] is not ((byte)' ' or (byte)'\t'))
                    break; // the next header: the value is complete
                value.Append(' ').Append(Encoding.ASCII.GetString(line).Trim());
            }
            else if (line.Length >= name.Length && Ascii.EqualsIgnoreCase(line[..name.Length], name))
            {
                value = new StringBuilder(Encoding.ASCII.GetString(line[name.Length..]).Trim());
            }
            if (end < 0)
                break;
            lineStart += end + 1;
        }
        return value?.ToString().Trim();
    }

    /// <summary>Parses <c>500ms</c>, <c>5s</c> or <c>1m</c> (whole numbers only).</summary>
    internal static bool TryParseDuration(string text, out TimeSpan duration)
    {
        duration = default;
        var (number, unit) = text.EndsWith("ms", StringComparison.OrdinalIgnoreCase) ? (text[..^2], 1)
            : text.EndsWith('s') || text.EndsWith('S') ? (text[..^1], 1000)
            : text.EndsWith('m') || text.EndsWith('M') ? (text[..^1], 60_000)
            : (string.Empty, 0);
        if (unit == 0 || number.Length is 0 or > 9 || !int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
            return false;
        duration = TimeSpan.FromMilliseconds((long)n * unit);
        return true;
    }

    private static HermexFaultRule Copy(HermexFaultRule r) => new()
    {
        Stage = r.Stage, Match = r.Match, ReplyCode = r.ReplyCode, Delay = r.Delay, Drop = r.Drop,
        Probability = r.Probability, EveryNth = r.EveryNth,
    };

    private sealed class Snapshot(bool enabled, HermexFaultRule[] rules)
    {
        public bool Enabled { get; } = enabled;
        public HermexFaultRule[] Rules { get; } = rules;
        public long[] Counters { get; } = new long[rules.Length];
    }
}
