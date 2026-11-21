using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Mail;
using System.Text;
using Hermex.Dashboard;
using Hermex.Smtp;
using Hermex.Storage;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Hermex.Tests;

/// <summary>ADR-0003 D14: relay recipient rules and .eml import.</summary>
public sealed class ImportAndRelayRulesTests
{
    private static readonly string[] AllowExample = { "*@example.test" };
    private static readonly string[] BlockBoss = { "boss@example.test" };
    private static readonly string[] None = Array.Empty<string>();
    private static readonly string[] OutsiderAndBoss = { "a@x.test", "boss@example.test" };
    private static readonly string[] NewPatterns = { "*@new.test", "qa+*@corp.test" };
    private static readonly string[] HeaderRecipients = { "ann@x.test", "bob@x.test", "hidden@x.test" };

    // ------------------------------------------------------------------ recipient rules

    private static string[] L(params string[] items) => items;

    private static RecipientRuleSet Rules(string[]? hostAllowed = null, string[]? hostBlocked = null,
        string[]? dashAllowed = null, string[]? dashBlocked = null) =>
        new(hostAllowed ?? None, hostBlocked ?? None, dashAllowed ?? None, dashBlocked ?? None);

    [Theory]
    [InlineData("ann@example.test", true)]
    [InlineData("ANN@Example.Test", true)]
    [InlineData("boss@example.test", false)]   // blocked beats allowed
    [InlineData("eve@elsewhere.test", false)]  // not allowed
    [InlineData("ann@example.test.", true)]    // trailing dot removed, then matched
    public void Blocked_beats_allowed(string recipient, bool kept) =>
        Assert.Equal(kept, HermexRelayOptions.FilterRecipients(new[] { recipient }, Rules(AllowExample, BlockBoss)).Count == 1);

    [Fact]
    public void Empty_allow_lists_allow_everyone_not_blocked()
    {
        var kept = HermexRelayOptions.FilterRecipients(OutsiderAndBoss, Rules(hostBlocked: BlockBoss));
        Assert.Equal("a@x.test", Assert.Single(kept));
    }

    [Fact]
    public void Dashboard_lists_only_narrow_the_host_lists()
    {
        // Host allows *@example.test; the dashboard allowing everything at elsewhere.test does not widen it.
        Assert.Empty(HermexRelayOptions.FilterRecipients(L("eve@elsewhere.test"),
            Rules(AllowExample, dashAllowed: L("*@elsewhere.test"))));
        // The dashboard can narrow: ann passes the host but not the dashboard list.
        Assert.Empty(HermexRelayOptions.FilterRecipients(L("ann@example.test"),
            Rules(AllowExample, dashAllowed: L("qa@example.test"))));
        // A dashboard block adds to the host's.
        Assert.Empty(HermexRelayOptions.FilterRecipients(L("ann@example.test"),
            Rules(AllowExample, dashBlocked: L("ann@*"))));
    }

    [Theory]
    [InlineData("victim@customer.com>x@example.test")]   // would match *@example.test as text
    [InlineData("victim@customer.com> <qa@example.test")]
    [InlineData("a b@example.test")]
    [InlineData("\"quoted\"@example.test")]
    [InlineData("@route:a@example.test")]
    [InlineData("a@b@example.test")]
    [InlineData("a\r@example.test")]
    [InlineData("ann@example..test")]
    [InlineData("ánn@example.test")]
    [InlineData("noat.example.test")]
    [InlineData("victim%gmail.com@example.test")]      // percent hack: an upstream may deliver to gmail.com
    [InlineData("gmail.com!victim@example.test")]      // bang path
    public void Recipients_that_are_not_plain_addresses_are_dropped(string recipient) =>
        Assert.Empty(HermexRelayOptions.FilterRecipients(L(recipient), Rules(L("*"))));

    [Fact]
    public void Host_patterns_with_stray_spaces_still_match()
    {
        var options = new HermexOptions();
        options.Relay.BlockedRecipients.Add(" *@customer.com ");
        options.Validate();
        Assert.Empty(HermexRelayOptions.FilterRecipients(L("bob@customer.com"), options.Relay.RecipientRules()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("two words@x.test")]
    [InlineData("a@x.test,b@x.test")]
    [InlineData("<a@x.test>")]
    [InlineData("*@customer.com.")]
    public void Invalid_patterns_fail_validation(string pattern)
    {
        var options = new HermexOptions();
        options.Relay.AllowedRecipients.Add(pattern);
        Assert.Throws<HermexConfigurationException>(options.Validate);
    }

    [Fact]
    public async Task Relay_sends_only_to_allowed_recipients_and_refuses_when_none_remain()
    {
        var seen = new ConcurrentQueue<IReadOnlyList<string>>();
        await using var host = await StartRelayHostAsync(seen, automatic: false);
        var relay = host.Services.GetRequiredService<IMailRelayService>();
        var store = host.Services.GetRequiredService<IMailStore>();

        var mixed = await AddAsync(store, "ann@example.test, eve@elsewhere.test, boss@example.test");
        var refusedByHook = await relay.RelayAsync(mixed);
        Assert.True(refusedByHook.Rejected);
        Assert.Equal("ann@example.test", Assert.Single(Assert.Single(seen)));

        var outsiders = await AddAsync(store, "eve@elsewhere.test, boss@example.test");
        var refused = await relay.RelayAsync(outsiders);
        Assert.True(refused.Rejected);
        Assert.Contains("recipient rules", refused.Error, StringComparison.Ordinal);
        Assert.Single(seen); // the hook never saw it

        // Without the request header (a cross-site simple POST) relay is refused before it runs.
        using (var crossSite = await host.Client.PostAsync($"/hermex/api/messages/{outsiders}/relay", null))
            Assert.Equal(HttpStatusCode.BadRequest, crossSite.StatusCode);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/hermex/api/messages/{outsiders}/relay");
        request.Headers.Add(Hermex.Internal.HermexRoutes.RequestHeader, "1");
        using var api = await host.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, api.StatusCode);
    }

    [Fact]
    public async Task Automatic_relay_applies_the_same_rules()
    {
        var seen = new ConcurrentQueue<IReadOnlyList<string>>();
        await using var host = await StartRelayHostAsync(seen, automatic: true);
        var port = await host.WaitForSmtpPortAsync();
        using (var client = new SmtpClient("127.0.0.1", port))
        using (var message = new MailMessage("app@example.test", "eve@elsewhere.test", "Blocked", "Body"))
            await client.SendMailAsync(message);
        using (var client = new SmtpClient("127.0.0.1", port))
        using (var message = new MailMessage("app@example.test", "ann@example.test", "Allowed", "Body"))
        {
            message.To.Add("boss@example.test");
            await client.SendMailAsync(message);
        }

        await Until(() => !seen.IsEmpty);
        await Task.Delay(200);
        Assert.Equal("ann@example.test", Assert.Single(Assert.Single(seen)));
    }

    [Fact]
    public async Task Settings_edit_the_dashboard_layer_and_never_the_host_lists()
    {
        await using var host = await HermexTestHost.StartAsync(o => o.Relay.AllowedRecipients.Add("*@code.test"));

        var current = await host.Client.GetFromJsonAsync<HermexSettings>("/hermex/api/settings");
        Assert.Empty(current!.RelayAllowedRecipients); // the dashboard layer starts empty

        current.RelayAllowedRecipients = new List<string> { "*@new.test", " qa+*@corp.test " };
        current.RelayBlockedRecipients = new List<string> { "boss@new.test" };
        using (var put = await host.Client.PutAsJsonAsync("/hermex/api/settings", current))
            Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal(NewPatterns, host.Options.Relay.DashboardRecipientRules().Allowed);
        Assert.Equal("boss@new.test", Assert.Single(host.Options.Relay.DashboardRecipientRules().Blocked));
        Assert.Equal("*@code.test", Assert.Single(host.Options.Relay.AllowedRecipients)); // untouched

        // Clearing the dashboard lists never clears the host's.
        current.RelayAllowedRecipients = new List<string>();
        current.RelayBlockedRecipients = new List<string>();
        using (var put = await host.Client.PutAsJsonAsync("/hermex/api/settings", current))
            Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal("*@code.test", Assert.Single(host.Options.Relay.AllowedRecipients));

        current.RelayBlockedRecipients = new List<string> { "bad pattern" };
        using var bad = await host.Client.PutAsJsonAsync("/hermex/api/settings", current);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task Settings_saved_before_the_lists_existed_load_with_an_empty_dashboard_layer()
    {
        // A 2.2 settings row has no recipient lists at all.
        await using var host = await HermexTestHost.StartAsync(o => o.Relay.AllowedRecipients.Add("*@code.test"),
            beforeUseHermex: async app =>
            {
                var store = app.Services.GetRequiredService<IMailStore>();
                await store.InitializeAsync();
                await store.SaveSettingsAsync(new Dictionary<string, string>
                {
                    ["hermex.settings"] = "{\"SmtpPort\":2525,\"ImapPort\":1143,\"ServerHostName\":\"old.test\",\"MaxMessageSizeBytes\":26214400,\"CaptureSessionTranscript\":true,\"DashboardTitle\":\"Hermex\",\"DefaultTheme\":\"System\",\"RelayHost\":\"localhost\",\"RelayPort\":25}",
                });
            });
        Assert.Equal("old.test", host.Options.ServerHostName); // the row was applied
        Assert.Equal("*@code.test", Assert.Single(host.Options.Relay.AllowedRecipients));
        Assert.Empty(host.Options.Relay.DashboardRecipientRules().Allowed);
    }

    [Fact]
    public async Task Relay_failures_log_the_reply_code_not_the_reply_text()
    {
        // A one-shot upstream that rejects the recipient, quoting the address as real servers do.
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var upstream = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII);
            await using var writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
            await writer.WriteLineAsync("220 upstream");
            while (await reader.ReadLineAsync() is { } line)
            {
                if (line.StartsWith("EHLO", StringComparison.Ordinal)) await writer.WriteLineAsync("250 upstream");
                else if (line.StartsWith("MAIL", StringComparison.Ordinal)) await writer.WriteLineAsync("250 ok");
                else if (line.StartsWith("RCPT", StringComparison.Ordinal)) { await writer.WriteLineAsync("550 5.1.1 <ann@example.test>: Recipient rejected"); }
                else if (line.StartsWith("QUIT", StringComparison.Ordinal)) { await writer.WriteLineAsync("221 bye"); break; }
                else await writer.WriteLineAsync("250 ok");
            }
        });

        await using var host = await HermexTestHost.StartAsync(o =>
        {
            o.Relay.Enabled = true;
            o.Relay.Host = "127.0.0.1";
            o.Relay.Port = port;
        });
        var store = host.Services.GetRequiredService<IMailStore>();
        var id = await AddAsync(store, "ann@example.test");
        var result = await host.Services.GetRequiredService<IMailRelayService>().RelayAsync(id);
        Assert.False(result.Success);
        Assert.Contains("550", result.Error, StringComparison.Ordinal); // the caller still gets the text

        IReadOnlyList<Hermex.Diagnostics.HermexLogEntry> logs = Array.Empty<Hermex.Diagnostics.HermexLogEntry>();
        await Until(() => (logs = store.GetLogsAsync(200).Result).Any(l => l.Category == "Relay" && l.Message.Contains("Failed", StringComparison.Ordinal)));
        var entry = logs.First(l => l.Category == "Relay" && l.Message.Contains("Failed", StringComparison.Ordinal));
        Assert.Contains("550", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("@", entry.Message, StringComparison.Ordinal);
        listener.Stop();
    }

    // ------------------------------------------------------------------ import

    [Fact]
    public void Envelope_comes_from_the_headers()
    {
        // Not plain addresses are dropped, as an SMTP envelope could never hold them.
        var odd = EmlImport.Build(TestSupport.Raw("From: a@x.test\nTo: <victim@evil.test> X=1@test.example>, ok@x.test\n\nBody\n"));
        Assert.Equal("ok@x.test", Assert.Single(odd.Recipients));
        // An explicit null Return-Path stays the null sender.
        Assert.Equal("", EmlImport.Build(TestSupport.Raw("Return-Path: <>\nFrom: MAILER-DAEMON@x.test\n\nBody\n")).MailFrom);
        // No usable sender: recorded as unknown, which relay refuses (never sent as <>).
        foreach (var raw in new[] { "From: undisclosed\nTo: a@x.test\n\nBody\n", "To: a@x.test\n\nBody\n" })
        {
            var sender = EmlImport.Build(TestSupport.Raw(raw)).MailFrom;
            Assert.NotEqual("", sender);                               // never the null sender
            Assert.Null(HermexRelayOptions.NormalizeAddress(sender));  // so relay refuses it
        }
        // A comment after the Return-Path address is ignored.
        Assert.Equal("a@b.test", EmlImport.Build(TestSupport.Raw("Return-Path: <a@b.test> (bounces)\n\nBody\n")).MailFrom);
        // A To: line in the body is not a header.
        Assert.Empty(EmlImport.Build(TestSupport.Raw("Subject: x\n\nTo: body@x.test\n")).Recipients);

        var message = EmlImport.Build(TestSupport.Raw(
            "Return-Path: <bounce@app.test>\nFrom: App <app@app.test>\nTo: Ann <ann@x.test>, bob@x.test\nCc: ANN@x.test\nBcc: hidden@x.test\nSubject: Hi\n\nBody\n"));
        Assert.Equal("bounce@app.test", message.MailFrom);
        Assert.Equal(HeaderRecipients, message.Recipients);
        Assert.Equal(EmlImport.Source, message.SessionId);
        Assert.True(message.Imported);

        Assert.Equal("app@app.test", EmlImport.Build(TestSupport.Raw("From: app@app.test\n\nBody\n")).MailFrom);
    }

    [Fact]
    public async Task Imported_files_are_stored_searchable_and_never_auto_relayed()
    {
        var seen = new ConcurrentQueue<IReadOnlyList<string>>();
        await using var host = await StartRelayHostAsync(seen, automatic: true);

        using var response = await Import(host, Form(
            ("one.eml", "From: app@app.test\r\nTo: ann@example.test\r\nSubject: Imported one\r\n\r\nBody\r\n"),
            ("two.eml", "From: app@app.test\r\nTo: ann@example.test\r\nSubject: Imported two\r\n\r\nBody\r\n"),
            ("empty.eml", "")));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"imported\":2", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var store = host.Services.GetRequiredService<IMailStore>();
        await Until(() => store.GetMessagesAsync(new MessageQuery { Search = "subject:imported" }).Result.TotalCount == 2);
        await Task.Delay(300);
        Assert.Empty(seen); // automatic relay is on, but imports are never relayed
    }

    [Fact]
    public async Task Import_needs_the_request_header_even_without_authorization_and_can_be_vetoed()
    {
        var vetoed = false;
        await using var host = await HermexTestHost.StartAsync(o => o.Events.OnImporting = ctx =>
        {
            if (vetoed) ctx.Reject("Imports are off.");
            return Task.CompletedTask;
        });
        using var crossSite = new HttpClient { BaseAddress = host.Client.BaseAddress };
        using (var response = await crossSite.PostAsync("/hermex/api/messages/import", Form(("a.eml", "Subject: x\r\n\r\nB"))))
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        vetoed = true;
        using var refused = await Import(host, Form(("a.eml", "Subject: x\r\n\r\nB")));
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains("Imports are off.", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Import_wakes_waiters()
    {
        await using var host = await HermexTestHost.StartAsync();
        var wait = host.Client.GetAsync("/hermex/api/messages/wait?subject=Waited&timeout=10");
        await Task.Delay(200);
        using (await Import(host, Form(("w.eml", "From: a@x.test\r\nTo: b@x.test\r\nSubject: Waited\r\n\r\nBody\r\n")))) { }
        using var result = await wait;
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
    }

    [Fact]
    public async Task Oversized_files_and_bad_requests_store_nothing()
    {
        await using var host = await HermexTestHost.StartAsync(o => o.MaxMessageSizeBytes = 2048);
        var big = "Subject: big\r\n\r\n" + new string('x', 4096);
        using (var response = await Import(host, Form(("ok.eml", "Subject: small\r\n\r\nBody"), ("big.eml", big))))
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);

        using (var empty = await Import(host, Form()))
            Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        using (var notForm = await Import(host, new StringContent("x")))
            Assert.Equal(HttpStatusCode.BadRequest, notForm.StatusCode);

        var tooMany = Enumerable.Range(0, EmlImport.MaxFiles + 1).Select(i => ($"{i}.eml", "Subject: n\r\n\r\nB")).ToArray();
        using (var many = await Import(host, Form(tooMany)))
            Assert.Equal(HttpStatusCode.BadRequest, many.StatusCode);

        await Task.Delay(200);
        Assert.Equal(0, (await host.Services.GetRequiredService<IMailStore>().GetMessagesAsync(new MessageQuery())).TotalCount);
    }

    // ------------------------------------------------------------------ helpers

    private static Task<HermexTestHost> StartRelayHostAsync(ConcurrentQueue<IReadOnlyList<string>> seen, bool automatic) =>
        HermexTestHost.StartAsync(o =>
        {
            o.Relay.Enabled = true;
            o.Relay.AutomaticRelay = automatic;
            o.Relay.Host = "smtp.example.test";
            o.Relay.AllowedRecipients.Add("*@example.test");
            o.Relay.BlockedRecipients.Add("boss@*");
            o.Events.OnRelaying = ctx =>
            {
                seen.Enqueue(ctx.Recipients.ToArray()); // capture, then stop before any connection
                ctx.Reject("Test: not connecting.");
                return Task.CompletedTask;
            };
        });

    private static async Task<long> AddAsync(IMailStore store, string envelopeTo)
    {
        var raw = TestSupport.Raw("From: app@example.test\nTo: x\nSubject: Relay\n\nBody\n");
        var record = new MessageRecord
        {
            SessionId = "test", ReceivedAtUtc = DateTimeOffset.UtcNow, EnvelopeFrom = "app@example.test",
            EnvelopeTo = envelopeTo, FromAddress = "app@example.test", FromDisplay = "app", ToDisplay = envelopeTo,
            Subject = "Relay", HasText = true, Recipients = envelopeTo.Split(", "), RawData = raw, RawSize = raw.Length,
        };
        await store.AddMessagesAsync(new[] { record });
        return record.Id;
    }

    /// <summary>Posts an import as the dashboard does, with the X-Hermex-Request header.</summary>
    private static async Task<HttpResponseMessage> Import(HermexTestHost host, HttpContent content)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/hermex/api/messages/import") { Content = content };
        request.Headers.Add(Hermex.Internal.HermexRoutes.RequestHeader, "1");
        return await host.Client.SendAsync(request);
    }

    private static MultipartFormDataContent Form(params (string Name, string Content)[] files)
    {
        var form = new MultipartFormDataContent();
        foreach (var (name, content) in files)
        {
            var part = new ByteArrayContent(Encoding.UTF8.GetBytes(content));
            part.Headers.ContentType = new MediaTypeHeaderValue("message/rfc822");
            form.Add(part, "files", name);
        }
        return form;
    }

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Condition not reached in 20 s.");
            await Task.Delay(25);
        }
    }
}
