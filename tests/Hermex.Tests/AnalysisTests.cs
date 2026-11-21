using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Hermex.Analysis;
using Hermex.Storage;
using Xunit;

namespace Hermex.Tests;

/// <summary>ADR-0003 D5 (HTML checks), D8 (preview hardening) and D13 (authentication headers).</summary>
public sealed class AnalysisTests
{
    private const string GoodHead =
        "<html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width\">" +
        "<title>T</title></head><body>";

    private static HeaderField[] NoHeaders => Array.Empty<HeaderField>();

    private static string[] Ids(string html, bool hasText = true, params HeaderField[] headers) =>
        HtmlChecks.Run(html, hasText, headers).Checks.Select(c => c.Id).ToArray();

    [Fact]
    public void Clean_html_has_no_findings() =>
        Assert.Equal(ExternalOnly, Ids(GoodHead +
            "<img src=\"https://cdn.test/a.png\" alt=\"Logo\" width=\"120\"><a href=\"https://shop.test/x\">Shop</a></body></html>"));

    private static readonly string[] ExternalOnly = { "external-resource" };
    private static readonly string[] Methods = { "dkim", "spf", "dmarc" };
    private static readonly string[] Results = { "pass", "softfail", "fail" };

    [Theory]
    [InlineData("<img src=\"a.png\">", "img-missing-alt")]
    [InlineData("<a href=\"http://shop.test\">x</a>", "insecure-http")]
    [InlineData("<img alt=\"\" src=\"http://shop.test/a.png\">", "insecure-http")]
    [InlineData("<img alt=\"\" src=\"https://cdn.test/a.png\">", "external-resource")]
    [InlineData("<link rel=\"stylesheet\" href=\"https://fonts.test/f.css\">", "external-resource")]
    [InlineData("<style>@import url('https://fonts.test/f.css');</style>", "external-resource")]
    [InlineData("<script>alert(1)</script>", "unsupported-element")]
    [InlineData("<form action=\"/x\"></form>", "unsupported-element")]
    [InlineData("<base href=\"https://evil.test/\">", "base-or-refresh")]
    [InlineData("<meta http-equiv=\"refresh\" content=\"0;url=https://evil.test\">", "base-or-refresh")]
    [InlineData("<a href=\"https://evil.test/login\">https://bank.test/login</a>", "link-text-mismatch")]
    [InlineData("<a href=\"https://evil.test\">www.bank.test</a>", "link-text-mismatch")]
    [InlineData("<a href=\"https://evil.test\">bank.test</a>", "link-text-mismatch")]
    [InlineData("<meta/http-equiv=\"refresh\" content=\"0\">", "base-or-refresh")]
    [InlineData("<img alt=\"\" src=\"https://t.test/p.gif\" width=\"1\" height=\"1\">", "tracking-pixel")]
    [InlineData("<img alt=\"\" src=\"https://t.test/p.gif\" style=\"width:1px; height:1px\">", "tracking-pixel")]
    public void Each_rule_fires(string body, string id) => Assert.Contains(id, Ids(GoodHead + body + "</body></html>"));

    [Theory]
    [InlineData("<img src=\"a.png\" alt=\"\">", "img-missing-alt")]                       // empty alt is valid (decorative)
    [InlineData("<a href=\"https://bank.test/login\">https://www.bank.test/login</a>", "link-text-mismatch")]
    [InlineData("<a href=\"https://bank.test\">Sign in</a>", "link-text-mismatch")]        // text is not an address
    [InlineData("<!-- <script>x</script> -->", "unsupported-element")]                   // comments are ignored
    [InlineData("<img alt=\"\" src=\"https://t.test/p.gif\" width=\"10\" height=\"1\">", "tracking-pixel")]
    [InlineData("<img alt=\"\" src=\"https://t.test/h.png\" style=\"border-width:0; line-height:0\">", "tracking-pixel")]
    [InlineData("<a href=\"https://cdn.test/x\">invoice.pdf</a>", "link-text-mismatch")]
    [InlineData("<a href=\"https://cdn.test/x\">index.html</a>", "link-text-mismatch")]
    public void Rule_does_not_fire_on_valid_html(string body, string id) =>
        Assert.DoesNotContain(id, Ids(GoodHead + body + "</body></html>"));

    [Fact]
    public void Document_level_rules()
    {
        var ids = Ids("<p>Hi</p>", hasText: false);
        Assert.Contains("no-text-part", ids);
        Assert.Contains("missing-title", ids);
        Assert.Contains("missing-lang", ids);
        Assert.Contains("missing-charset", ids);
        Assert.Contains("missing-viewport", ids);
        Assert.Equal("warning", HtmlChecks.Run("<p>Hi</p>", false, NoHeaders).Checks[0].Severity); // warnings sort first
    }

    [Fact]
    public void Gmail_clipping_counts_utf8_bytes()
    {
        var under = GoodHead + new string('a', 100_000) + "</body></html>";
        var over = GoodHead + string.Concat(Enumerable.Repeat("é", 51_000)) + "</body></html>"; // 2 bytes each
        Assert.DoesNotContain("gmail-clipping", Ids(under));
        Assert.Contains("gmail-clipping", Ids(over));
    }

    [Fact]
    public void Links_are_listed_once_with_counts_and_schemes()
    {
        var report = HtmlChecks.Run(GoodHead +
            "<a href=\"https://a.test\">A <b>link</b></a><a href=\"https://a.test\">again</a><a href=\"mailto:x@y.test\">Mail</a>" +
            "</body></html>", true, NoHeaders);
        Assert.Equal(2, report.Links.Count);
        Assert.Equal(new HtmlLink("https://a.test", "A link", "https", 2), report.Links[0]);
        Assert.Equal("mailto", report.Links[1].Scheme);
    }

    [Theory]
    [InlineData(null, "one-click-unsubscribe")]
    [InlineData("List-Unsubscribe=One-Click", null)]
    public void One_click_unsubscribe_needs_both_headers(string? post, string? expected)
    {
        var headers = new List<HeaderField> { new("List-Unsubscribe", "<https://u.test/x>, <mailto:u@u.test>") };
        if (post is not null)
            headers.Add(new HeaderField("List-Unsubscribe-Post", post));
        var ids = HtmlChecks.Run(null, true, headers).Checks.Select(c => c.Id).ToArray();
        if (expected is null) Assert.Empty(ids); else Assert.Contains(expected, ids);
    }

    [Fact]
    public void Samples_are_bounded()
    {
        var report = HtmlChecks.Run(GoodHead + "<img src=\"" + new string('x', 5000) + "\"></body></html>", true, NoHeaders);
        var sample = report.Checks.Single(c => c.Id == "img-missing-alt").Sample!;
        Assert.True(sample.Length <= HtmlChecks.MaxSampleLength);
    }

    [Theory]
    [InlineData("<a ")]               // unclosed tags
    [InlineData("<a href=\"")]        // unclosed attribute quotes
    [InlineData("<!--")]              // unclosed comment
    [InlineData("<a href=x>")]        // anchors never closed
    public void Hostile_input_is_capped_and_linear(string unit)
    {
        var html = string.Concat(Enumerable.Repeat(unit, (3 * 1024 * 1024) / unit.Length));
        var watch = Stopwatch.StartNew();
        var report = HtmlChecks.Run(html, true, NoHeaders);
        watch.Stop();
        Assert.True(report.Truncated);
        Assert.Contains(report.Checks, c => c.Id == "scan-truncated");
        // Generous bound for slow CI; backtracking engines take minutes or time out on these inputs.
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), $"took {watch.Elapsed}");

        watch.Restart();
        HtmlChecks.StripBaseAndRefresh(html); // the preview strip runs on the whole, uncapped body
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), $"strip took {watch.Elapsed}");
    }

    [Fact]
    public void Base_and_refresh_are_stripped_for_the_preview()
    {
        var html = "<head><BASE href=\"https://evil.test/\"><meta http-equiv='Refresh' content='0;url=x'><meta charset=utf-8></head>";
        Assert.Equal("<head><meta charset=utf-8></head>", HtmlChecks.StripBaseAndRefresh(html));
        Assert.Equal("x", HtmlChecks.StripBaseAndRefresh("<ba<base>se href=\"https://evil.test/\">x"));
        Assert.Equal("x", HtmlChecks.StripBaseAndRefresh("<base/href=\"https://evil.test/\">x"));
        Assert.Equal("x", HtmlChecks.StripBaseAndRefresh("<meta/http-equiv=\"refresh\"/content=\"0;url=https://evil.test\">x"));
        Assert.Equal("x", HtmlChecks.StripBaseAndRefresh("<meta content=\">\" http-equiv=refresh content=\"0;url=y\">x"));
        Assert.Equal("x", HtmlChecks.StripBaseAndRefresh("<meta http-equiv=\"&#x72;efresh\" content=\"0;url=y\">x"));
        Assert.Equal("<meta name=\"viewport\" content=\"a>b\">", HtmlChecks.StripBaseAndRefresh("<meta name=\"viewport\" content=\"a>b\">"));
    }

    // ------------------------------------------------------------ authentication headers

    [Fact]
    public void Authentication_results_are_parsed()
    {
        var header = AuthenticationHeaders.ParseResults(
            "mx.google.com; dkim=pass header.i=@shop.test header.s=s1 (comment; with semicolon); " +
            "spf=softfail (domain of transitioning x@y does not designate) smtp.mailfrom=x@shop.test; " +
            "dmarc=fail reason=\"policy; quarantine\" header.from=shop.test", arc: false);
        Assert.Equal("mx.google.com", header.AuthServId);
        Assert.Equal(Methods, header.Results.Select(r => r.Method));
        Assert.Equal(Results, header.Results.Select(r => r.Result));
        Assert.Equal("s1", header.Results[0].Properties["header.s"]);
        Assert.Equal("policy; quarantine", header.Results[2].Reason);
    }

    [Fact]
    public void Whitespace_around_equals_is_allowed()
    {
        var header = AuthenticationHeaders.ParseResults("mx.test; dkim = pass header.d = shop.test; spf= (x) fail", arc: false);
        Assert.Equal(new[] { ("dkim", "pass"), ("spf", "fail") }, header.Results.Select(r => (r.Method, r.Result)));
        Assert.Equal("shop.test", header.Results[0].Properties["header.d"]);
    }

    [Fact]
    public void Overlong_signature_says_it_was_cut()
    {
        var value = "v=1; a=rsa-sha256; d=x.test; s=s; h=" + new string('a', AuthenticationHeaders.MaxHeaderLength) + "; bh=x; b=y";
        var report = AuthenticationHeaders.Parse(new[] { new HeaderField("DKIM-Signature", value) });
        Assert.True(report.Truncated);
        Assert.StartsWith("Header is longer than", Assert.Single(report.DkimSignatures[0].Warnings), StringComparison.Ordinal);
    }

    [Fact]
    public void None_and_arc_instance_are_handled()
    {
        Assert.Empty(AuthenticationHeaders.ParseResults("mx.test 1; none", arc: false).Results);
        var arc = AuthenticationHeaders.ParseResults("i=2; mx.test; arc=pass", arc: true);
        Assert.Equal(2, arc.Instance);
        Assert.Equal("arc", Assert.Single(arc.Results).Method);
    }

    [Fact]
    public void Received_spf_is_parsed()
    {
        var spf = AuthenticationHeaders.ParseReceivedSpf(
            "Pass (mx.test: domain of a@shop.test designates 192.0.2.1 as permitted sender) client-ip=192.0.2.1; envelope-from=\"a@shop.test\";");
        Assert.Equal("pass", spf.Result);
        Assert.StartsWith("mx.test: domain", spf.Comment);
        Assert.Equal("192.0.2.1", spf.Properties["client-ip"]);
        Assert.Equal("a@shop.test", spf.Properties["envelope-from"]);
    }

    [Fact]
    public void Dkim_signature_structure_warnings()
    {
        var good = AuthenticationHeaders.ParseSignature("DKIM-Signature",
            "v=1; a=rsa-sha256; c=relaxed/relaxed; d=shop.test; s=s1; h=from:to:subject; bh=abc=; b=" + new string('A', 300));
        Assert.Empty(good.Warnings);
        Assert.Equal("shop.test", good.Tags["d"]);
        Assert.True(good.Tags["b"].Length <= 81);

        var bad = AuthenticationHeaders.ParseSignature("DKIM-Signature", "v=1; a=rsa-sha1; d=x.test; l=100; t=200; x=100");
        Assert.Contains(bad.Warnings, w => w.StartsWith("Missing required tag(s): b, bh, h, s", StringComparison.Ordinal));
        Assert.Contains(bad.Warnings, w => w.Contains("rsa-sha1", StringComparison.Ordinal));
        Assert.Contains(bad.Warnings, w => w.StartsWith("l=", StringComparison.Ordinal));
        Assert.Contains(bad.Warnings, w => w.StartsWith("x= (expiry) is not after", StringComparison.Ordinal));
    }

    [Fact]
    public void Arc_headers_are_grouped_by_instance_and_bounded()
    {
        var headers = new List<HeaderField>
        {
            new("ARC-Seal", "i=2; a=rsa-sha256; cv=pass; d=b.test; s=s; b=x"),
            new("ARC-Seal", "i=1; a=rsa-sha256; cv=none; d=a.test; s=s; b=x"),
            new("ARC-Message-Signature", "i=1; a=rsa-sha256; d=a.test; s=s; h=from; bh=x; b=x"),
            new("ARC-Seal", "i=99; cv=pass"),
        };
        headers.AddRange(Enumerable.Range(0, 60).Select(_ => new HeaderField("Authentication-Results", "mx.test; spf=pass")));
        var report = AuthenticationHeaders.Parse(headers);
        Assert.Equal(new int?[] { 1, 1, 2, null }, report.ArcSignatures.Select(s => s.Instance));
        Assert.NotEmpty(report.ArcSignatures[3].Warnings);
        Assert.Equal(AuthenticationHeaders.MaxHeadersPerKind, report.AuthenticationResults.Count);
        Assert.True(report.Truncated);
    }

    [Fact]
    public void Comments_nest_and_quotes_are_kept() =>
        Assert.Equal("a  \"(kept)\" b", AuthenticationHeaders.StripComments("a (x (y) z) \"(kept)\" b"));

    // ------------------------------------------------------------ API

    [Fact]
    public async Task Checks_endpoint_and_preview_hardening()
    {
        await using var host = await HermexTestHost.StartAsync();
        var id = await host.SeedMessageAsync(
            "From: a@shop.test\nTo: b@example.test\nSubject: Html\n" +
            "Authentication-Results: mx.test; dkim=pass header.d=shop.test\n" +
            "MIME-Version: 1.0\nContent-Type: text/html; charset=utf-8\n\n" +
            "<html><head><base href=\"https://evil.test/\"></head><body><img src=\"x.png\"></body></html>\n");

        using var checks = await host.Client.GetAsync($"/hermex/api/messages/{id}/checks");
        Assert.Equal(HttpStatusCode.OK, checks.StatusCode);
        using var json = JsonDocument.Parse(await checks.Content.ReadAsStringAsync());
        var ids = json.RootElement.GetProperty("html").GetProperty("checks").EnumerateArray()
            .Select(c => c.GetProperty("id").GetString()).ToArray();
        Assert.Contains("no-text-part", ids);
        Assert.Contains("img-missing-alt", ids);
        Assert.Contains("base-or-refresh", ids);
        var result = json.RootElement.GetProperty("authentication").GetProperty("authenticationResults")[0];
        Assert.Equal("mx.test", result.GetProperty("authServId").GetString());

        var html = await host.Client.GetStringAsync($"/hermex/api/messages/{id}/html");
        Assert.DoesNotContain("<base", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<img src=\"x.png\">", html, StringComparison.Ordinal);

        // Reading checks must not mark the message read.
        var store = host.Services.GetService(typeof(IMailStore)) as IMailStore;
        Assert.False((await store!.GetMessageAsync(id))!.IsRead);

        using var missing = await host.Client.GetAsync("/hermex/api/messages/9999/checks");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }
}
