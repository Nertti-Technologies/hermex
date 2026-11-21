using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Hermex.Internal;
using Hermex.Storage;

namespace Hermex.Analysis;

/// <summary>One finding of the HTML checks (ADR-0003 D5).</summary>
/// <param name="Id">Stable rule id, e.g. <c>img-missing-alt</c>.</param>
/// <param name="Severity"><c>error</c>, <c>warning</c> or <c>info</c>.</param>
/// <param name="Message">What was found and why it matters.</param>
/// <param name="Count">How many times it was found.</param>
/// <param name="Sample">The first occurrence, at most <see cref="HtmlChecks.MaxSampleLength"/> characters.</param>
internal sealed record HtmlCheckResult(string Id, string Severity, string Message, int Count, string? Sample);

/// <summary>One unique link target in the HTML body.</summary>
internal sealed record HtmlLink(string Href, string Text, string Scheme, int Count);

/// <summary>The result of <see cref="HtmlChecks.Run"/>.</summary>
internal sealed record HtmlCheckReport(
    bool HasHtml,
    long HtmlBytes,
    bool Truncated,
    IReadOnlyList<HtmlCheckResult> Checks,
    IReadOnlyList<HtmlLink> Links,
    bool LinksTruncated);

/// <summary>
/// Built-in checks over a stored message's HTML body and a few headers (ADR-0003 D5). No network access, no
/// stored state. Every regular expression uses <see cref="RegexOptions.NonBacktracking"/>, so matching time is
/// linear in the input; input is capped at <see cref="MaxScanChars"/>. Worst case for a hostile message: a few
/// linear passes over 2 Mi characters and at most <see cref="MaxLinks"/> link entries in memory.
/// </summary>
internal static class HtmlChecks
{
    /// <summary>Characters of HTML scanned (estimate: 2 Mi, far above real email; Hermex's own cap).</summary>
    public const int MaxScanChars = 2 * 1024 * 1024;

    /// <summary>Gmail clips messages whose HTML exceeds about 102 KB. Not published by Google; figure from
    /// vendor tests cited in ADR-0003 D5 (estimate).</summary>
    public const int GmailClipBytes = 102_000;

    /// <summary>Longest sample shown per finding.</summary>
    public const int MaxSampleLength = 120;

    /// <summary>Most unique links listed (estimate: well above real campaigns); the rest are counted only.</summary>
    public const int MaxLinks = 500;

    private const RegexOptions Options =
        RegexOptions.NonBacktracking | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    /// <summary>Backstop only; matching is linear. Estimate: the worst hostile 2 Mi-character inputs in
    /// AnalysisTests take under 1 s, so 5 s leaves room for slow machines.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    /// <summary>Hosts named in the external-resource finding (estimate: enough to recognise a sender).</summary>
    private const int MaxHostsShown = 10;

    /// <summary>Link text longer than this is prose, not an address (estimate; URLs in link text are short).</summary>
    private const int MaxUrlLikeTextLength = 200;

    private static readonly Regex Comment = new("<!--.*?-->", Options | RegexOptions.Singleline, Timeout);
    // Browsers accept '/' between attributes and '>' inside quoted values, so both are handled here.
    private static readonly Regex Tag = new(@"<([a-z][a-z0-9]*)([\s/](""[^""]*""|'[^']*'|[^>""'])*)?>", Options, Timeout);
    private static readonly Regex Attribute =
        new(@"([a-z_:][-a-z0-9_:.]*)(\s*=\s*(""[^""]*""|'[^']*'|[^\s""'>]+))?", Options, Timeout);
    private static readonly Regex Anchor = new(@"<a(\s[^>]*)?>(.*?)</a\s*>", Options | RegexOptions.Singleline, Timeout);
    private static readonly Regex AnyTag = new("<[^>]*>", Options, Timeout);
    private static readonly Regex CssImport = new(@"@import\s+(url\()?\s*[""']?(https?:[^""')\s;]+)", Options, Timeout);
    private static readonly Regex CssUrl = new(@"url\(\s*[""']?(https?:[^""')\s]+)", Options, Timeout);
    private static readonly Regex UrlLikeText = new(@"^(https?://)?([a-z0-9-]+\.)+[a-z]{2,}(/\S*)?$", Options, Timeout);

    private static readonly HashSet<string> FileExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "pdf", "htm", "html", "txt", "csv", "ics", "vcf", "doc", "docx", "xls", "xlsx", "ppt", "pptx", "zip",
        "png", "jpg", "jpeg", "gif", "svg", "webp", "mp3", "mp4", "mov", "eml", "json", "xml", "php", "asp", "aspx",
    };

    private static readonly HashSet<string> UnsupportedElements =
        new(StringComparer.OrdinalIgnoreCase) { "script", "form", "iframe", "object", "embed", "video" };

    private const string Removed = "\u200B";

    // The preview strip's own tag pattern, read the way a browser tokenizer reads tags (WHATWG): a quote opens a
    // value only right after "="; anywhere else (an attribute name, an unquoted value) it is an ordinary
    // character. The shared Tag pattern pairs quotes anywhere, which is fine for the checks but would let it
    // absorb a real <base> into a tag it keeps (<p a"><base href=…>">).
    private static readonly Regex BrowserTag = new(@"<([a-z][a-z0-9]*)([\s/](?:=\s*""[^""]*""|=\s*'[^']*'|[^>])*)?>", Options, Timeout);

    /// <summary>
    /// Removes <c>&lt;base&gt;</c> and <c>&lt;meta http-equiv=refresh&gt;</c> before the preview is served
    /// (ADR-0003 D8): email clients strip both, and they would retarget or navigate the preview frame.
    /// </summary>
    public static string StripBaseAndRefresh(string html)
    {
        // One pass, linear in the input (a repeat-until-stable loop is quadratic on nested input). A removed tag
        // becomes a zero-width space, which an HTML tokenizer keeps inside a tag name (WHATWG "tag name state":
        // only whitespace, "/" and ">" end it), so removal cannot splice a new tag together: "<base<base> href>"
        // becomes a tag named "base\u200B", and "<ba<base>se>" one named "ba\u200Bse", neither of them base.
        // This is preview fidelity, not a security boundary (the frame is sandboxed and served with CSP sandbox).
        // Attributes are decoded exactly as the checks do, so "&#x72;efresh" and "<meta/http-equiv=refresh>"
        // are caught too.
        return BrowserTag.Replace(html, m =>
        {
            var name = m.Groups[1].Value;
            var candidate = name.Equals("base", StringComparison.OrdinalIgnoreCase) || name.Equals("meta", StringComparison.OrdinalIgnoreCase);
            return candidate && IsBaseOrRefresh(name, ParseAttributes(m.Groups[2].Value)) ? Removed : m.Value;
        });
    }

    private static bool IsBaseOrRefresh(string tagName, Dictionary<string, string> attributes) =>
        tagName.Equals("base", StringComparison.OrdinalIgnoreCase)
        || (tagName.Equals("meta", StringComparison.OrdinalIgnoreCase)
            && string.Equals(attributes.GetValueOrDefault("http-equiv"), "refresh", StringComparison.OrdinalIgnoreCase));

    /// <summary>Runs every rule. <paramref name="headers"/> is the message's header list.</summary>
    public static HtmlCheckReport Run(string? html, bool hasTextPart, IReadOnlyList<HeaderField> headers)
    {
        var checks = new List<HtmlCheckResult>();
        CheckUnsubscribe(headers, checks);

        if (string.IsNullOrEmpty(html))
            return new HtmlCheckReport(false, 0, false, checks, Array.Empty<HtmlLink>(), false);

        var htmlBytes = Encoding.UTF8.GetByteCount(html);
        var truncated = html.Length > MaxScanChars;
        var scan = Comment.Replace(truncated ? html[..MaxScanChars] : html, string.Empty);

        if (!hasTextPart)
            checks.Add(new("no-text-part", "warning",
                "No text/plain alternative. Text-only clients and some spam filters expect one.", 1, null));
        if (htmlBytes > GmailClipBytes)
            checks.Add(new("gmail-clipping", "warning",
                $"HTML is {htmlBytes:N0} bytes; Gmail clips messages over about {GmailClipBytes:N0} bytes.", 1, null));

        var tally = new Tally();
        var hosts = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var tagNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var htmlHasLang = false;
        var hasCharset = false;
        var hasViewport = false;

        foreach (Match tag in Tag.Matches(scan))
        {
            var name = tag.Groups[1].Value;
            tagNames.Add(name);
            var attributes = ParseAttributes(tag.Groups[2].Value);
            var source = tag.Value;

            switch (name.ToLowerInvariant())
            {
                case "img":
                    if (!attributes.ContainsKey("alt"))
                        tally.Add("img-missing-alt", source);
                    if (IsTrackingPixel(attributes))
                        tally.Add("tracking-pixel", source);
                    NoteResource(attributes.GetValueOrDefault("src"), source, tally, hosts);
                    break;
                case "link" when attributes.GetValueOrDefault("rel")?.Contains("stylesheet", StringComparison.OrdinalIgnoreCase) == true:
                    NoteResource(attributes.GetValueOrDefault("href"), source, tally, hosts);
                    break;
                case "base":
                    tally.Add("base-or-refresh", source);
                    break;
                case "meta":
                    if (IsBaseOrRefresh(name, attributes))
                        tally.Add("base-or-refresh", source);
                    if (attributes.ContainsKey("charset")
                        || attributes.GetValueOrDefault("content")?.Contains("charset=", StringComparison.OrdinalIgnoreCase) == true)
                        hasCharset = true;
                    if (string.Equals(attributes.GetValueOrDefault("name"), "viewport", StringComparison.OrdinalIgnoreCase))
                        hasViewport = true;
                    break;
                case "html":
                    htmlHasLang |= attributes.ContainsKey("lang");
                    break;
            }

            if (UnsupportedElements.Contains(name))
                tally.Add("unsupported-element", source);
            if (attributes.GetValueOrDefault("href") is { } href && href.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                tally.Add("insecure-http", source);
            else if (attributes.GetValueOrDefault("src") is { } src && src.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                tally.Add("insecure-http", source);
        }

        foreach (Match import in CssImport.Matches(scan))
            NoteResource(import.Groups[2].Value, import.Value, tally, hosts);
        foreach (Match url in CssUrl.Matches(scan))
            NoteResource(url.Groups[1].Value, url.Value, tally, hosts);

        var (links, linksTruncated) = CollectLinks(scan, tally);

        tally.Emit(checks, "unsupported-element", "warning",
            "script, form, iframe, object, embed or video: removed or unsupported by most email clients.");
        tally.Emit(checks, "base-or-refresh", "warning",
            "<base> or <meta http-equiv=refresh>: clients strip these; the preview removes them too.");
        tally.Emit(checks, "link-text-mismatch", "warning",
            "Link text shows a different address than the link goes to, a common phishing pattern.");
        tally.Emit(checks, "img-missing-alt", "warning", "Images without alt text are unreadable with images off or a screen reader.");
        tally.Emit(checks, "insecure-http", "warning", "Links or images over http:// (no TLS).");
        tally.Emit(checks, "external-resource", "info",
            "External images, fonts or stylesheets load from: " + string.Join(", ", hosts.Take(MaxHostsShown)) + (hosts.Count > MaxHostsShown ? ", …" : "") + ".");
        tally.Emit(checks, "tracking-pixel", "info", "1×1 image, the usual shape of an open-tracking pixel.");

        if (!tagNames.Contains("title"))
            checks.Add(new("missing-title", "info", "No <title>; some clients and screen readers show it.", 1, null));
        if (!htmlHasLang)
            checks.Add(new("missing-lang", "info", "No lang attribute on <html>; screen readers may use the wrong language.", 1, null));
        if (!hasCharset)
            checks.Add(new("missing-charset", "info", "No <meta charset>; the MIME charset is used instead.", 1, null));
        if (!hasViewport)
            checks.Add(new("missing-viewport", "info", "No viewport <meta>; mobile clients may render at desktop width.", 1, null));
        if (truncated)
            checks.Add(new("scan-truncated", "info",
                $"Only the first {MaxScanChars:N0} characters were checked.", 1, null));

        checks.Sort((a, b) => Rank(a.Severity).CompareTo(Rank(b.Severity)));
        return new HtmlCheckReport(true, htmlBytes, truncated, checks, links, linksTruncated);
    }

    private static int Rank(string severity) => severity switch { "error" => 0, "warning" => 1, _ => 2 };

    /// <summary>RFC 8058 §3.1: one-click unsubscribe needs both headers; Gmail requires it of bulk senders.</summary>
    private static void CheckUnsubscribe(IReadOnlyList<HeaderField> headers, List<HtmlCheckResult> checks)
    {
        var unsubscribe = headers.FirstOrDefault(h => h.Name.Equals("List-Unsubscribe", StringComparison.OrdinalIgnoreCase));
        if (unsubscribe is null)
            return;
        var post = headers.FirstOrDefault(h => h.Name.Equals("List-Unsubscribe-Post", StringComparison.OrdinalIgnoreCase));
        if (post is null || !post.Value.Replace(" ", "", StringComparison.Ordinal)
                .Contains("List-Unsubscribe=One-Click", StringComparison.OrdinalIgnoreCase))
            checks.Add(new("one-click-unsubscribe", "warning",
                "List-Unsubscribe without List-Unsubscribe-Post: List-Unsubscribe=One-Click (RFC 8058). " +
                "Gmail and Yahoo require one-click unsubscribe from bulk senders.", 1, Shorten(unsubscribe.Value)));
        else if (!unsubscribe.Value.Contains("<https:", StringComparison.OrdinalIgnoreCase))
            checks.Add(new("one-click-unsubscribe", "warning",
                "One-click unsubscribe needs an https: URI in List-Unsubscribe (RFC 8058 §3.1).", 1, Shorten(unsubscribe.Value)));
    }

    private static (IReadOnlyList<HtmlLink>, bool) CollectLinks(string scan, Tally tally)
    {
        var links = new Dictionary<string, (string Text, int Count)>(StringComparer.Ordinal);
        var truncated = false;
        foreach (Match anchor in Anchor.Matches(scan))
        {
            var attributes = ParseAttributes(anchor.Groups[1].Value);
            if (attributes.GetValueOrDefault("href") is not { Length: > 0 } href)
                continue;
            var text = WebUtility.HtmlDecode(AnyTag.Replace(anchor.Groups[2].Value, " ")).Trim();
            text = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

            if (LinkTextMismatch(text, href))
                tally.Add("link-text-mismatch", anchor.Value);

            if (links.TryGetValue(href, out var existing))
                links[href] = (existing.Text, existing.Count + 1);
            else if (links.Count < MaxLinks)
                links[href] = (text, 1);
            else
                truncated = true;
        }

        var list = links
            .Select(kv => new HtmlLink(kv.Key, Shorten(kv.Value.Text) ?? string.Empty, SchemeOf(kv.Key), kv.Value.Count))
            .ToList();
        return (list, truncated);
    }

    /// <summary>True when the visible text looks like an address whose host differs from the target host.</summary>
    internal static bool LinkTextMismatch(string text, string href)
    {
        if (text.Length == 0 || text.Length > MaxUrlLikeTextLength || !UrlLikeText.IsMatch(text))
            return false;
        // "invoice.pdf" or "index.html" looks like a domain; without a scheme or "www." treat known file
        // extensions as file names, not addresses.
        if (!text.Contains("://", StringComparison.Ordinal) && !text.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
            && !text.Contains('/', StringComparison.Ordinal) && FileExtensions.Contains(text[(text.LastIndexOf('.') + 1)..]))
            return false;
        var shown = HostOf(text.Contains("://", StringComparison.Ordinal) ? text : "https://" + text);
        var target = HostOf(href);
        if (shown is null || target is null)
            return false;
        static string Bare(string host) => host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..] : host;
        return !string.Equals(Bare(shown), Bare(target), StringComparison.OrdinalIgnoreCase);
    }

    private static string? HostOf(string url) =>
        Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? uri.IdnHost : null;

    private static string SchemeOf(string href)
    {
        var colon = href.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0 || colon > 20)
            return "relative";
        var scheme = href[..colon].ToLowerInvariant();
        return scheme.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '+' or '-' or '.') ? scheme : "relative";
    }

    private static void NoteResource(string? url, string source, Tally tally, SortedSet<string> hosts)
    {
        if (url is null || HostOf(url) is not { } host)
            return;
        tally.Add("external-resource", source);
        hosts.Add(host);
    }

    private static bool IsTrackingPixel(Dictionary<string, string> attributes)
    {
        static bool IsOne(string? value) => value?.Replace("!important", "", StringComparison.OrdinalIgnoreCase)
            .Trim().TrimEnd('x', 'p', 'X', 'P').Trim() is "1" or "0";
        if (IsOne(attributes.GetValueOrDefault("width")) && IsOne(attributes.GetValueOrDefault("height")))
            return true;
        // Compare declaration names exactly, so line-height or border-width do not count.
        string? width = null, height = null;
        foreach (var declaration in (attributes.GetValueOrDefault("style") ?? string.Empty).Split(';'))
        {
            var colon = declaration.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
                continue;
            var property = declaration[..colon].Trim();
            if (property.Equals("width", StringComparison.OrdinalIgnoreCase)) width = declaration[(colon + 1)..];
            else if (property.Equals("height", StringComparison.OrdinalIgnoreCase)) height = declaration[(colon + 1)..];
        }
        return IsOne(width) && IsOne(height);
    }

    internal static Dictionary<string, string> ParseAttributes(string text)
    {
        var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (text.Length == 0)
            return attributes;
        foreach (Match match in Attribute.Matches(text))
        {
            var value = match.Groups[3].Success ? match.Groups[3].Value : string.Empty;
            if (value.Length >= 2 && value[0] is '"' or '\'' && value[^1] == value[0])
                value = value[1..^1];
            attributes.TryAdd(match.Groups[1].Value, WebUtility.HtmlDecode(value).Trim());
        }
        return attributes;
    }

    private static string? Shorten(string? value) =>
        value is null ? null : TextLimits.Truncate(value.ReplaceLineEndings(" "), MaxSampleLength);

    /// <summary>Counts findings per rule and keeps the first sample of each.</summary>
    private sealed class Tally
    {
        private readonly Dictionary<string, (int Count, string Sample)> _items = new(StringComparer.Ordinal);

        public void Add(string id, string sample) =>
            _items[id] = _items.TryGetValue(id, out var item) ? (item.Count + 1, item.Sample) : (1, Shorten(sample)!);

        public void Emit(List<HtmlCheckResult> checks, string id, string severity, string message)
        {
            if (_items.TryGetValue(id, out var item))
                checks.Add(new HtmlCheckResult(id, severity, message, item.Count, item.Sample));
        }
    }
}
