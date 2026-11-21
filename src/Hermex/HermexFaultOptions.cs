using System.Text.Json.Serialization;

namespace Hermex;

/// <summary>The SMTP step a fault rule applies to (ADR-0003 D11).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<HermexSmtpStage>))]
public enum HermexSmtpStage
{
    /// <summary>A new connection, before the greeting.</summary>
    Connect = 0,

    /// <summary>HELO or EHLO.</summary>
    Helo = 1,

    /// <summary>AUTH.</summary>
    Auth = 2,

    /// <summary>MAIL FROM; <see cref="HermexFaultRule.Match"/> is matched against the sender.</summary>
    MailFrom = 3,

    /// <summary>RCPT TO; <see cref="HermexFaultRule.Match"/> is matched against each recipient.</summary>
    RcptTo = 4,

    /// <summary>The end of the message data, before it is stored: a fault here means the message is not captured.</summary>
    Data = 5,
}

/// <summary>
/// SMTP fault injection for testing how an application handles a failing mail server (ADR-0003 D11): rejected
/// commands, slow replies and dropped connections. Off by default, and refused outside the Development
/// environment unless <see cref="AllowOutsideDevelopment"/> is set: on a shared server one rule fails the
/// whole team's mail. Each injected fault is written to the diagnostic log (rule, stage and reply; no addresses).
/// </summary>
public sealed class HermexFaultOptions
{
    /// <summary>Most rules allowed (estimate: a test scenario uses a handful).</summary>
    public const int MaxRules = 50;

    /// <summary>Turns fault injection on. Default <c>false</c>.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Allows <see cref="Enabled"/> outside the Development environment. Default <c>false</c>: the host then
    /// refuses to start with faults enabled, and the dashboard refuses to enable them.
    /// </summary>
    public bool AllowOutsideDevelopment { get; set; }

    /// <summary>The rules, checked in order; the first that fires at a stage wins.</summary>
    public IList<HermexFaultRule> Rules { get; } = new List<HermexFaultRule>();

    /// <summary>
    /// Lets a message ask for its own fault with an <c>X-Hermex-Fault</c> header, applied at the end of DATA:
    /// a reply code (<c>451</c>), <c>drop</c>, or <c>delay=5s</c> (also <c>500ms</c>, <c>1m</c>), comma-separated.
    /// Only while <see cref="Enabled"/>. Default <c>true</c>.
    /// </summary>
    public bool AllowHeaderTrigger { get; set; } = true;

    /// <summary>Seed for <see cref="HermexFaultRule.Probability"/>, for repeatable runs. Null uses a random seed.</summary>
    public int? RandomSeed { get; set; }

    internal void Validate(TimeSpan sessionTimeout)
    {
        if (Rules.Count > MaxRules)
            throw new HermexConfigurationException($"HermexFaultOptions.Rules allows at most {MaxRules} rules.");
        for (var i = 0; i < Rules.Count; i++)
        {
            var error = Rules[i] is { } rule ? rule.Validate(sessionTimeout) : "is null";
            if (error is not null)
                throw new HermexConfigurationException($"HermexFaultOptions.Rules[{i}] {error}");
        }
    }
}

/// <summary>
/// One fault rule. When it fires it waits <see cref="Delay"/> (if set), then either closes the connection
/// (<see cref="Drop"/>), replies with <see cref="ReplyCode"/>, or lets the command continue normally (a delay only).
/// A 421 reply also closes the connection, as RFC 5321 §3.8 requires.
/// </summary>
public sealed class HermexFaultRule
{
    /// <summary>Longest delay in any case (estimate: far beyond any client timeout worth testing).</summary>
    public static readonly TimeSpan MaxDelay = TimeSpan.FromMinutes(10);

    /// <summary>The longest delay allowed: SessionTimeout when it is set, never more than <see cref="MaxDelay"/>.</summary>
    internal static TimeSpan MaxDelayFor(TimeSpan sessionTimeout) =>
        sessionTimeout <= TimeSpan.Zero || sessionTimeout > MaxDelay ? MaxDelay : sessionTimeout; // infinite (-1 ms) → cap

    /// <summary>Longest <see cref="Match"/> pattern (estimate: RFC 5321's 256-octet path plus wildcards).</summary>
    public const int MaxMatchLength = Internal.AddressPattern.MaxLength;

    /// <summary>The SMTP step this rule applies to.</summary>
    public HermexSmtpStage Stage { get; set; }

    /// <summary>
    /// For <see cref="HermexSmtpStage.MailFrom"/> and <see cref="HermexSmtpStage.RcptTo"/> only: a pattern for the
    /// address, where <c>*</c> matches any run of characters and <c>?</c> one character, ignoring case
    /// (<c>*@bounce.test</c>). Null matches any address.
    /// </summary>
    public string? Match { get; set; }

    /// <summary>The reply to send instead of the normal one, 400–599. Null with no <see cref="Drop"/> means delay only.</summary>
    public int? ReplyCode { get; set; }

    /// <summary>Wait before replying: up to <see cref="HermexOptions.SessionTimeout"/> and at most <see cref="MaxDelay"/>.</summary>
    public TimeSpan? Delay { get; set; }

    /// <summary>Close the connection without a reply.</summary>
    public bool Drop { get; set; }

    /// <summary>Chance of firing, 0–100. Default 100. Ignored when <see cref="EveryNth"/> is set.</summary>
    public int Probability { get; set; } = 100;

    /// <summary>Fire on every Nth time the rule matches (1 = every time), instead of by chance.</summary>
    public int? EveryNth { get; set; }

    /// <summary>Returns null when valid, otherwise what is wrong (completing "Rules[i] …").</summary>
    internal string? Validate(TimeSpan sessionTimeout)
    {
        if (!Enum.IsDefined(Stage))
            return "has an unknown Stage.";
        if (Match is not null && Stage is not (HermexSmtpStage.MailFrom or HermexSmtpStage.RcptTo))
            return "sets Match, which applies to the MailFrom and RcptTo stages only.";
        if (Match is { Length: 0 or > MaxMatchLength })
            return $"Match must be 1–{MaxMatchLength} characters.";
        if (ReplyCode is < 400 or > 599)
            return "ReplyCode must be a failure reply, 400–599.";
        if (Delay is { } delay && (delay < TimeSpan.Zero || delay > MaxDelayFor(sessionTimeout)))
            return $"Delay must be between zero and {MaxDelayFor(sessionTimeout).TotalSeconds:0} s (SessionTimeout, at most 10 minutes).";
        if (ReplyCode is null && !Drop && Delay is null)
            return "does nothing: set ReplyCode, Drop or Delay.";
        if (ReplyCode is not null && Drop)
            return "sets both ReplyCode and Drop.";
        if (Probability is < 0 or > 100)
            return "Probability must be 0–100.";
        if (EveryNth is < 1)
            return "EveryNth must be at least 1.";
        return null;
    }
}
