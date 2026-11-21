using System.Globalization;
using System.Text;
using Hermex.Internal;
using Hermex.Storage;

namespace Hermex.Analysis;

/// <summary>One <c>method=result</c> entry of an Authentication-Results header (RFC 8601 §2.2).</summary>
internal sealed record AuthResult(string Method, string Result, string? Reason, IReadOnlyDictionary<string, string> Properties);

/// <summary>A parsed Authentication-Results or ARC-Authentication-Results header.</summary>
internal sealed record AuthResultsHeader(string AuthServId, int? Instance, IReadOnlyList<AuthResult> Results);

/// <summary>A parsed Received-SPF header (RFC 7208 §9.1).</summary>
internal sealed record ReceivedSpf(string Result, string? Comment, IReadOnlyDictionary<string, string> Properties);

/// <summary>A DKIM-Signature or ARC-Message-Signature / ARC-Seal header's tags with offline structural warnings.</summary>
internal sealed record SignatureHeader(string Kind, int? Instance, IReadOnlyDictionary<string, string> Tags, IReadOnlyList<string> Warnings);

/// <summary>Everything <see cref="AuthenticationHeaders.Parse"/> found.</summary>
internal sealed record AuthenticationReport(
    IReadOnlyList<AuthResultsHeader> AuthenticationResults,
    IReadOnlyList<ReceivedSpf> ReceivedSpf,
    IReadOnlyList<SignatureHeader> DkimSignatures,
    IReadOnlyList<AuthResultsHeader> ArcAuthenticationResults,
    IReadOnlyList<SignatureHeader> ArcSignatures,
    bool Truncated);

/// <summary>
/// Parses authentication headers for display only (ADR-0003 D13). Nothing is verified: no DNS, no cryptography.
/// The headers are whatever the sending path wrote, so the dashboard labels them as unverified
/// (RFC 8601 §5). Bounded: at most <see cref="MaxHeadersPerKind"/> headers of each kind (RFC 8617 §4.2.1 caps ARC
/// at 50 instances), each at most <see cref="MaxHeaderLength"/> characters, every loop linear in that length.
/// </summary>
internal static class AuthenticationHeaders
{
    /// <summary>Headers parsed per kind (RFC 8617 §4.2.1: ARC instances are 1–50).</summary>
    public const int MaxHeadersPerKind = 50;

    /// <summary>Characters parsed per header (estimate: real headers are under 2 KB; a 2048-bit b= is ~350).</summary>
    public const int MaxHeaderLength = 8 * 1024;

    /// <summary>b= and bh= are shortened to this for display (estimate: enough to tell signatures apart);
    /// the full value stays in the header table.</summary>
    private const int MaxTagDisplayLength = 80;

    private static readonly string[] RequiredDkimTags = { "v", "a", "b", "bh", "d", "h", "s" };
    private static readonly string[] Base64Tags = { "b", "bh" };
    private static readonly System.Buffers.SearchValues<char> SpfResultEnd = System.Buffers.SearchValues.Create(" \t(");

    public static AuthenticationReport Parse(IReadOnlyList<HeaderField> headers)
    {
        var truncated = false;
        List<HeaderField> Of(string name)
        {
            var all = headers.Where(h => h.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (all.Count > MaxHeadersPerKind)
                truncated = true;
            return all.Take(MaxHeadersPerKind).ToList();
        }

        string Bound(string value)
        {
            if (value.Length <= MaxHeaderLength)
                return value;
            truncated = true;
            return value[..MaxHeaderLength];
        }

        SignatureHeader Signature(string kind, string value)
        {
            truncated |= value.Length > MaxHeaderLength;
            return ParseSignatureBounded(kind, value);
        }

        var results = Of("Authentication-Results").Select(h => ParseResults(Bound(h.Value), arc: false)).ToList();
        var spf = Of("Received-SPF").Select(h => ParseReceivedSpf(Bound(h.Value))).ToList();
        var dkim = Of("DKIM-Signature").Select(h => Signature("DKIM-Signature", h.Value)).ToList();
        var arcResults = Of("ARC-Authentication-Results").Select(h => ParseResults(Bound(h.Value), arc: true))
            .OrderBy(r => r.Instance ?? int.MaxValue).ToList();
        var arcSignatures = Of("ARC-Seal").Select(h => Signature("ARC-Seal", h.Value))
            .Concat(Of("ARC-Message-Signature").Select(h => Signature("ARC-Message-Signature", h.Value)))
            .OrderBy(s => s.Instance ?? int.MaxValue).ThenBy(s => s.Kind, StringComparer.Ordinal).ToList();

        return new AuthenticationReport(results, spf, dkim, arcResults, arcSignatures, truncated);
    }

    private static SignatureHeader ParseSignatureBounded(string kind, string value)
    {
        if (value.Length <= MaxHeaderLength)
            return ParseSignature(kind, value);
        // A cut signature would report its later tags as missing; say what happened instead.
        var cut = ParseSignature(kind, value[..MaxHeaderLength]);
        return cut with { Warnings = new[] { $"Header is longer than {MaxHeaderLength:N0} characters; only the start was parsed." } };
    }

    /// <summary>RFC 8601 §2.2: <c>authserv-id [version] *(";" method=result [reason] *(ptype.prop=value)) / "; none"</c>.
    /// For ARC (RFC 8617 §4.1.1) the value starts with <c>i=N;</c>.</summary>
    internal static AuthResultsHeader ParseResults(string value, bool arc)
    {
        var parts = SplitOutsideQuotes(JoinAroundEquals(StripComments(value)), ';');
        var index = 0;
        int? instance = null;
        if (arc && parts.Count > 0 && TryInstance(parts[0], out var i))
        {
            instance = i;
            index = 1;
        }

        var authServId = index < parts.Count ? parts[index].Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "" : "";
        var list = new List<AuthResult>();
        foreach (var part in parts.Skip(index + 1))
        {
            var words = SplitOutsideQuotes(part, ' ');
            if (words.Count == 0 || words[0].Equals("none", StringComparison.OrdinalIgnoreCase))
                continue;
            var (method, result) = KeyValue(words[0]);
            string? reason = null;
            var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var word in words.Skip(1))
            {
                var (key, val) = KeyValue(word);
                if (key.Equals("reason", StringComparison.OrdinalIgnoreCase))
                    reason = val;
                else if (key.Length > 0)
                    properties.TryAdd(key, val);
            }
            list.Add(new AuthResult(method.ToLowerInvariant(), result.ToLowerInvariant(), reason, properties));
        }
        return new AuthResultsHeader(authServId, instance, list);
    }

    /// <summary>RFC 7208 §9.1: <c>result [ (comment) ] *( key=value ; )</c>. Parsed leniently (see errata).</summary>
    internal static ReceivedSpf ParseReceivedSpf(string value)
    {
        var trimmed = value.Trim();
        var space = trimmed.AsSpan().IndexOfAny(SpfResultEnd);
        var result = (space < 0 ? trimmed : trimmed[..space]).ToLowerInvariant();
        var rest = space < 0 ? string.Empty : trimmed[space..];

        string? comment = null;
        var open = rest.IndexOf('(', StringComparison.Ordinal);
        if (open >= 0 && rest[..open].Trim().Length == 0)
        {
            var close = MatchingParen(rest, open);
            comment = rest[(open + 1)..(close < 0 ? rest.Length : close)].Trim();
            rest = close < 0 ? string.Empty : rest[(close + 1)..];
        }

        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in SplitOutsideQuotes(rest, ';'))
        {
            var (key, val) = KeyValue(part);
            if (key.Length > 0)
                properties.TryAdd(key, val);
        }
        return new ReceivedSpf(result, comment, properties);
    }

    /// <summary>RFC 6376 §3.2 tag-list (<c>tag=value;</c>), with §3.5 structural checks for DKIM-Signature.</summary>
    internal static SignatureHeader ParseSignature(string kind, string value)
    {
        var tags = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in value.Split(';'))
        {
            var eq = part.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
                continue;
            var tag = part[..eq].Trim().ToLowerInvariant();
            var tagValue = RemoveWhitespace(part[(eq + 1)..]);
            if (tag.Length > 0)
                tags.TryAdd(tag, tagValue);
        }

        var warnings = new List<string>();
        int? instance = tags.TryGetValue("i", out var iValue) && kind != "DKIM-Signature" && TryInstance("i=" + iValue, out var i) ? i : null;
        if (kind == "DKIM-Signature")
        {
            var missing = RequiredDkimTags.Where(t => !tags.ContainsKey(t)).ToList();
            if (missing.Count > 0)
                warnings.Add("Missing required tag(s): " + string.Join(", ", missing) + " (RFC 6376 §3.5).");
            if (tags.TryGetValue("v", out var v) && v != "1")
                warnings.Add($"v={v}; only version 1 is defined.");
            if (tags.TryGetValue("a", out var a) && a.Equals("rsa-sha1", StringComparison.OrdinalIgnoreCase))
                warnings.Add("a=rsa-sha1 is no longer accepted by verifiers (RFC 8301).");
            if (tags.ContainsKey("l"))
                warnings.Add("l= signs only part of the body, so content can be appended without breaking the signature.");
            if (TryLong(tags, "t", out var t) && TryLong(tags, "x", out var x) && x <= t)
                warnings.Add("x= (expiry) is not after t= (signing time) (RFC 6376 §3.5).");
            if (TryLong(tags, "x", out var expiry) && expiry < DateTimeOffset.UtcNow.ToUnixTimeSeconds())
                warnings.Add("Signature has expired (x=).");
        }
        else if (instance is null)
        {
            warnings.Add("Missing or invalid i= (ARC instance must be 1–50, RFC 8617 §4.2.1).");
        }

        // Shorten long base64 values for display; the full header stays in the header table.
        foreach (var key in Base64Tags)
        {
            if (tags.TryGetValue(key, out var long64))
                tags[key] = TextLimits.Truncate(long64, MaxTagDisplayLength + 1);
        }
        return new SignatureHeader(kind, instance, tags, warnings);
    }

    private static bool TryInstance(string part, out int instance)
    {
        instance = 0;
        var (key, value) = KeyValue(part.Trim());
        return key.Equals("i", StringComparison.OrdinalIgnoreCase)
               && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out instance)
               && instance is >= 1 and <= MaxHeadersPerKind;
    }

    private static bool TryLong(Dictionary<string, string> tags, string tag, out long value)
    {
        value = 0;
        return tags.TryGetValue(tag, out var text)
               && long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    private static (string Key, string Value) KeyValue(string text)
    {
        var eq = text.IndexOf('=', StringComparison.Ordinal);
        if (eq < 0)
            return (text.Trim(), string.Empty);
        var value = text[(eq + 1)..].Trim();
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            value = value[1..^1];
        return (text[..eq].Trim(), value);
    }

    /// <summary>Removes RFC 5322 comments (nested parentheses), outside quoted strings. Linear.</summary>
    internal static string StripComments(string value)
    {
        var builder = new StringBuilder(value.Length);
        var depth = 0;
        var quoted = false;
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c == '\\' && i + 1 < value.Length && (quoted || depth > 0))
            {
                if (depth == 0)
                    builder.Append(c).Append(value[i + 1]);
                i++;
                continue;
            }
            if (!quoted && c == '(') { depth++; continue; }
            if (!quoted && c == ')' && depth > 0) { depth--; continue; }
            if (depth > 0)
                continue;
            if (c == '"')
                quoted = !quoted;
            builder.Append(c);
        }
        return builder.ToString();
    }

    /// <summary>RFC 8601 allows CFWS around "=" ("dkim = pass"); removes that whitespace outside quotes. Linear.</summary>
    internal static string JoinAroundEquals(string value)
    {
        var builder = new StringBuilder(value.Length);
        var quoted = false;
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c == '"')
                quoted = !quoted;
            if (!quoted && c == '=')
            {
                while (builder.Length > 0 && char.IsWhiteSpace(builder[^1]))
                    builder.Length--;
                builder.Append('=');
                while (i + 1 < value.Length && char.IsWhiteSpace(value[i + 1]))
                    i++;
                continue;
            }
            builder.Append(c);
        }
        return builder.ToString();
    }

    private static List<string> SplitOutsideQuotes(string value, char separator)
    {
        var parts = new List<string>();
        var start = 0;
        var quoted = false;
        for (var i = 0; i <= value.Length; i++)
        {
            if (i < value.Length && value[i] == '"')
                quoted = !quoted;
            var split = i == value.Length || (!quoted && (separator == ' ' ? char.IsWhiteSpace(value[i]) : value[i] == separator));
            if (!split)
                continue;
            var part = value[start..i].Trim();
            if (part.Length > 0)
                parts.Add(part);
            start = i + 1;
        }
        return parts;
    }

    private static int MatchingParen(string text, int open)
    {
        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            if (text[i] == '(') depth++;
            else if (text[i] == ')' && --depth == 0) return i;
        }
        return -1;
    }

    private static string RemoveWhitespace(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (!char.IsWhiteSpace(c))
                builder.Append(c);
        }
        return builder.ToString();
    }
}
