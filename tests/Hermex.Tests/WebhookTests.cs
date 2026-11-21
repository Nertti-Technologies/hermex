using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hermex.Background;
using Hermex.Internal;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Hermex.Tests;

/// <summary>ADR-0003 D4: outbound webhooks.</summary>
public sealed class WebhookTests
{
    private static readonly string Secret = "whsec_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    private static readonly string[] FailedFailedDelivered = { "Failed", "Failed", "Delivered" };
    private static readonly TimeSpan[] FastRetries = { TimeSpan.FromMilliseconds(50) };

    // ------------------------------------------------------------------ signing and network guard

    [Fact]
    public void Signature_matches_the_standard_webhooks_test_vector()
    {
        // Test vector from the Standard Webhooks reference libraries (secret, id, timestamp, payload → signature).
        Assert.True(WebhookSigner.TryDecodeSecret("whsec_MfKQ9r8GKYqrTwjUPD8ILPZIo2LaLaSw", out var key));
        var signature = WebhookSigner.Sign("msg_p5jXN8AQM9LWM0D4loKWxJek", 1614265330,
            Encoding.UTF8.GetBytes("{\"test\": 2432232314}"), new[] { key });
        Assert.Equal("v1,g0hM9SsE+OTPJTGt/tmIKtSyZlE3uFJELVlNIOLJ1OE=", signature);
    }

    [Theory]
    [InlineData("secret")]
    [InlineData("whsec_")]
    [InlineData("whsec_c2hvcnQ=")]                    // 5 bytes: too short
    [InlineData("whsec_not base64!")]
    public void Bad_secrets_are_refused(string secret) => Assert.False(WebhookSigner.TryDecodeSecret(secret, out _));

    [Theory]
    // address, allowPrivate, allowed
    [InlineData("169.254.169.254", true, false)]      // cloud metadata: always blocked
    [InlineData("::ffff:169.254.169.254", true, false)] // ...also when IPv4-mapped
    [InlineData("64:ff9b::a9fe:a9fe", true, false)]   // ...or NAT64-embedded
    [InlineData("2002:a9fe:a9fe::1", true, false)]    // ...or 6to4-embedded
    [InlineData("fe80::1", true, false)]
    [InlineData("0.0.0.0", true, false)]
    [InlineData("::", true, false)]
    [InlineData("224.0.0.1", true, false)]
    [InlineData("ff02::1", true, false)]
    [InlineData("255.255.255.255", true, false)]
    [InlineData("168.63.129.16", true, false)]        // Azure WireServer
    [InlineData("100.100.100.200", true, false)]      // Alibaba metadata
    [InlineData("fd00:ec2::254", true, false)]        // AWS IMDS over IPv6
    [InlineData("::a9fe:a9fe", true, false)]          // IPv4-compatible 169.254.169.254
    [InlineData("::ffff:0:a9fe:a9fe", true, false)]   // IPv4-translated 169.254.169.254
    [InlineData("::a00:5", false, false)]             // IPv4-compatible 10.0.0.5
    [InlineData("::1", true, true)]                   // loopback is not unwrapped to 0.0.0.1
    [InlineData("127.0.0.1", true, true)]
    [InlineData("127.0.0.1", false, false)]
    [InlineData("::1", false, false)]
    [InlineData("::ffff:127.0.0.1", false, false)]
    [InlineData("10.1.2.3", false, false)]
    [InlineData("172.20.0.1", false, false)]
    [InlineData("172.32.0.1", false, true)]           // just outside 172.16.0.0/12
    [InlineData("192.168.1.1", false, false)]
    [InlineData("100.64.0.1", false, false)]
    [InlineData("fd00::1", false, false)]
    [InlineData("93.184.216.34", false, true)]
    [InlineData("2606:2800:220:1:248:1893:25c8:1946", false, true)]
    public void Network_guard(string address, bool allowPrivate, bool allowed) =>
        Assert.Equal(allowed, WebhookNetworkGuard.IsAllowed(IPAddress.Parse(address), allowPrivate));

    [Theory]
    [InlineData(HermexWebhookNetworkPolicy.Auto, true, true)]
    [InlineData(HermexWebhookNetworkPolicy.Auto, false, false)]   // fails closed outside Development
    [InlineData(HermexWebhookNetworkPolicy.AllowPrivate, false, true)]
    [InlineData(HermexWebhookNetworkPolicy.PublicOnly, true, false)]
    public void Policy_resolution(HermexWebhookNetworkPolicy policy, bool development, bool allowPrivate) =>
        Assert.Equal(allowPrivate, WebhookNetworkGuard.AllowsPrivate(policy, development));

    [Theory]
    [InlineData("ftp://x.test/", "whsec_MfKQ9r8GKYqrTwjUPD8ILPZIo2LaLaSw")]
    [InlineData("https://user:pw@x.test/", "whsec_MfKQ9r8GKYqrTwjUPD8ILPZIo2LaLaSw")]
    [InlineData("https://x.test/", "not-a-secret")]
    public void Invalid_endpoints_fail_validation(string url, string secret)
    {
        var options = new HermexOptions();
        options.Webhooks.Endpoints.Add(new HermexWebhookEndpoint { Url = new Uri(url), Secret = secret });
        Assert.Throws<HermexConfigurationException>(options.Validate);
    }

    // ------------------------------------------------------------------ delivery

    [Fact]
    public async Task Notice_is_signed_metadata_only()
    {
        await using var receiver = await Receiver.StartAsync(_ => 200);
        await using var host = await StartHostAsync(receiver.Url);

        await SendAsync(host, "Welcome <b>secret body</b>", cc: "audit@example.test");
        var request = await receiver.NextAsync();

        var id = request.Headers["webhook-id"];
        var timestamp = long.Parse(request.Headers["webhook-timestamp"], System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(WebhookSigner.TryDecodeSecret(Secret, out var key));
        Assert.Equal(WebhookSigner.Sign(id, timestamp, request.Body, new[] { key }), request.Headers["webhook-signature"]);
        Assert.Equal("application/json", request.ContentType);

        using var json = JsonDocument.Parse(request.Body);
        Assert.Equal("message.received", json.RootElement.GetProperty("type").GetString());
        var data = json.RootElement.GetProperty("data");
        Assert.Equal("Hello", data.GetProperty("subject").GetString());
        Assert.Equal("app@example.test", data.GetProperty("from").GetString());
        Assert.Equal("audit@example.test", data.GetProperty("cc")[0].GetString());
        Assert.DoesNotContain("secret body", Encoding.UTF8.GetString(request.Body), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Failures_are_retried_with_the_same_id_and_a_redirect_is_not_followed()
    {
        var calls = 0;
        await using var receiver = await Receiver.StartAsync(_ => Interlocked.Increment(ref calls) switch { 1 => 500, 2 => 302, _ => 204 });
        await using var host = await StartHostAsync(receiver.Url);

        await SendAsync(host, "Body");
        var first = await receiver.NextAsync();
        var second = await receiver.NextAsync();
        var third = await receiver.NextAsync();

        Assert.Equal(first.Headers["webhook-id"], second.Headers["webhook-id"]);
        Assert.Equal(first.Headers["webhook-id"], third.Headers["webhook-id"]);
        var service = host.Services.GetRequiredService<WebhookService>();
        await Until(() => service.Deliveries.Any(d => d.Outcome == "Delivered"));
        Assert.Equal(FailedFailedDelivered, service.Deliveries.Reverse().Select(d => d.Outcome));
        Assert.Equal("Redirects are not followed", service.Deliveries[1].Error);
    }

    [Fact]
    public async Task Gone_disables_the_endpoint_until_resumed()
    {
        await using var receiver = await Receiver.StartAsync(_ => 410);
        await using var host = await StartHostAsync(receiver.Url);
        var service = host.Services.GetRequiredService<WebhookService>();

        await SendAsync(host, "Body");
        await Until(() => service.Endpoints[0].State == "Disabled");
        await SendAsync(host, "Body");
        await Until(() => service.Skipped > 0); // counted, not logged per notice

        using var resume = await host.Client.PostAsync("/hermex/api/webhooks/0/resume", null);
        Assert.Equal(HttpStatusCode.OK, resume.StatusCode);
        Assert.Equal("Active", service.Endpoints[0].State);
        using var missing = await host.Client.PostAsync("/hermex/api/webhooks/5/resume", null);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Theory]
    [InlineData("http://169.254.169.254/latest/meta-data/", HermexWebhookNetworkPolicy.AllowPrivate)]
    [InlineData("http://localhost:9/hook", HermexWebhookNetworkPolicy.PublicOnly)] // a name resolving to loopback
    public async Task Blocked_addresses_are_never_connected(string url, HermexWebhookNetworkPolicy policy)
    {
        await using var host = await StartHostAsync(new Uri(url), o => o.Webhooks.NetworkPolicy = policy);
        var service = host.Services.GetRequiredService<WebhookService>();

        await SendAsync(host, "Body");
        await Until(() => service.Deliveries.Count > 0);
        var delivery = Assert.Single(service.Deliveries);
        Assert.Equal("Blocked", delivery.Outcome);
        Assert.Null(delivery.StatusCode);
    }

    [Fact]
    public async Task Hook_can_add_headers_or_veto()
    {
        await using var receiver = await Receiver.StartAsync(_ => 200);
        var veto = false;
        await using var host = await StartHostAsync(receiver.Url, o => o.Events.OnWebhookSending = ctx =>
        {
            if (veto) ctx.Reject("Not now.");
            else ctx.Headers["X-Audit"] = "case-42";
            return Task.CompletedTask;
        });
        var service = host.Services.GetRequiredService<WebhookService>();

        await SendAsync(host, "Body");
        Assert.Equal("case-42", (await receiver.NextAsync()).Headers["X-Audit"]);

        veto = true;
        await SendAsync(host, "Body");
        await Until(() => service.Deliveries.Any(d => d.Outcome == "Vetoed"));
        Assert.Equal(1, receiver.Count);
    }

    [Fact]
    public async Task Non_ascii_hook_header_rejects_the_attempt()
    {
        await using var receiver = await Receiver.StartAsync(_ => 200);
        await using var host = await StartHostAsync(receiver.Url, o => o.Events.OnWebhookSending = ctx =>
        {
            ctx.Headers["X-User"] = "José";
            return Task.CompletedTask;
        });
        var service = host.Services.GetRequiredService<WebhookService>();

        await SendAsync(host, "Body");
        await Until(() => service.Deliveries.Count > 0);
        Assert.Equal("Vetoed", Assert.Single(service.Deliveries).Outcome);
        Assert.Equal(0, service.Endpoints[0].ConsecutiveFailures);
    }

    [Fact]
    public async Task Notice_is_capped_for_huge_headers()
    {
        await using var receiver = await Receiver.StartAsync(_ => 200);
        await using var host = await StartHostAsync(receiver.Url);
        // Raw SMTP: one envelope recipient, 200 addresses in the Cc header only (SmtpClient would send each as RCPT).
        var port = await host.WaitForSmtpPortAsync();
        using (var client = new System.Net.Sockets.TcpClient())
        {
            await client.ConnectAsync(IPAddress.Loopback, port);
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII);
            await using var writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
            async Task Say(string line, string expect)
            {
                if (line.Length > 0) await writer.WriteLineAsync(line);
                string? reply;
                do reply = await reader.ReadLineAsync(); while (reply is { Length: > 3 } && reply[3] == '-');
                Assert.StartsWith(expect, reply);
            }
            await Say("", "220");
            await Say("EHLO test", "250");
            await Say("MAIL FROM:<app@example.test>", "250");
            await Say("RCPT TO:<ann@example.test>", "250");
            await Say("DATA", "354");
            var cc = string.Join(",\r\n ", Enumerable.Range(0, 200).Select(i => $"cc{i}@example.test"));
            await writer.WriteLineAsync($"From: app@example.test\r\nTo: ann@example.test\r\nCc: {cc}\r\nSubject: {new string('S', 5000)}\r\n\r\nBody");
            await Say(".", "250");
            await Say("QUIT", "221");
        }

        var request = await receiver.NextAsync();
        Assert.True(request.Body.Length < 20_000, $"payload was {request.Body.Length} bytes");
        using var json = JsonDocument.Parse(request.Body);
        var data = json.RootElement.GetProperty("data");
        Assert.True(data.GetProperty("truncated").GetBoolean());
        Assert.Equal(20, data.GetProperty("cc").GetArrayLength());
    }

    [Fact]
    public async Task A_hanging_receiver_never_slows_capture()
    {
        await using var receiver = await Receiver.StartAsync(_ => 200, delay: TimeSpan.FromSeconds(30));
        await using var host = await StartHostAsync(receiver.Url);
        var store = host.Services.GetRequiredService<Hermex.Storage.IMailStore>();

        var started = DateTime.UtcNow;
        for (var i = 0; i < 3; i++)
            await SendAsync(host, "Body");
        await Until(() => store.GetMessagesAsync(new Hermex.Storage.MessageQuery()).Result.TotalCount == 3);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Delivery_log_never_shows_the_query_string()
    {
        await using var receiver = await Receiver.StartAsync(_ => 200);
        await using var host = await StartHostAsync(new Uri(receiver.Url, "?token=s3cret"));
        await SendAsync(host, "Body");
        await receiver.NextAsync();

        var json = await host.Client.GetStringAsync("/hermex/api/webhooks/deliveries");
        Assert.DoesNotContain("s3cret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("/hook", json, StringComparison.Ordinal); // the path may carry a token too
        Assert.Contains("\"enabled\":true", json, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ helpers

    private static Task<HermexTestHost> StartHostAsync(Uri url, Action<HermexOptions>? configure = null) =>
        HermexTestHost.StartAsync(o =>
        {
            o.Webhooks.Endpoints.Add(new HermexWebhookEndpoint { Url = url, Secret = Secret });
            o.Webhooks.MaxAttempts = 3;
            o.Webhooks.Timeout = TimeSpan.FromSeconds(2);
            o.Webhooks.RetryDelaysOverride = FastRetries;
            configure?.Invoke(o);
        });

    private static async Task SendAsync(HermexTestHost host, string body, string? cc = null)
    {
        var port = await host.WaitForSmtpPortAsync();
        using var client = new SmtpClient("127.0.0.1", port);
        using var message = new MailMessage("app@example.test", "ann@example.test", "Hello", body);
        if (cc is not null) message.CC.Add(cc);
        await client.SendMailAsync(message);
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

    private sealed record Captured(Dictionary<string, string> Headers, string? ContentType, byte[] Body);

    /// <summary>A real HTTP receiver on loopback that records each request and answers with a chosen status.</summary>
    private sealed class Receiver : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly ConcurrentQueue<Captured> _requests = new();
        private readonly SemaphoreSlim _arrived = new(0);

        private Receiver(WebApplication app) => _app = app;

        public Uri Url => new(_app.Urls.First() + "/hook");
        public int Count => _requests.Count;

        public static async Task<Receiver> StartAsync(Func<int, int> status, TimeSpan? delay = null)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            var app = builder.Build();
            var receiver = new Receiver(app);
            var n = 0;
            app.MapPost("/hook", async (HttpContext http) =>
            {
                using var buffer = new MemoryStream();
                await http.Request.Body.CopyToAsync(buffer);
                receiver._requests.Enqueue(new Captured(
                    http.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase),
                    http.Request.ContentType, buffer.ToArray()));
                receiver._arrived.Release();
                if (delay is { } d) await Task.Delay(d, http.RequestAborted);
                var code = status(Interlocked.Increment(ref n));
                if (code is >= 300 and < 400) http.Response.Headers.Location = "http://169.254.169.254/";
                return Results.StatusCode(code);
            });
            await app.StartAsync();
            return receiver;
        }

        public async Task<Captured> NextAsync()
        {
            Assert.True(await _arrived.WaitAsync(TimeSpan.FromSeconds(20)), "No webhook arrived in 20 s.");
            return _requests.ElementAt(_requests.Count - _arrived.CurrentCount - 1);
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
            _arrived.Dispose();
        }
    }
}
