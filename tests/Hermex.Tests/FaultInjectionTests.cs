using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using Hermex.Diagnostics;
using Hermex.Smtp;
using Hermex.Storage;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Hermex.Tests;

/// <summary>ADR-0003 D11: SMTP fault injection.</summary>
public sealed class FaultInjectionTests
{
    private static readonly bool[] EveryThird = { false, false, true, false, false, true, false, false, true };
    private static readonly string[] OneAndThree = { "one", "three" };

    // ------------------------------------------------------------------ units

    [Theory]
    [InlineData("*@bounce.test", "ann@BOUNCE.test", true)]
    [InlineData("*@bounce.test", "ann@bounce.test.evil", false)]
    [InlineData("a?n@*", "ann@x", true)]
    [InlineData("a?n@*", "an@x", false)]
    [InlineData("*", "", true)]
    [InlineData("**x**", "abcx", true)]
    [InlineData("*b", "*ab", true)]                 // '*' in the text does not stop the wildcard
    [InlineData("exact@x.test", "exact@x.test", true)]
    [InlineData("exact@x.test", "exact@x.tes", false)]
    public void Glob_matches(string pattern, string text, bool expected) =>
        Assert.Equal(expected, FaultInjector.Glob(pattern, text));

    [Fact]
    public void Glob_is_fast_on_hostile_patterns()
    {
        var watch = Stopwatch.StartNew();
        Assert.False(FaultInjector.Glob(string.Concat(Enumerable.Repeat("*a", 150)) + "b", new string('a', 4000)));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), $"took {watch.Elapsed}");
    }

    [Theory]
    [InlineData("500ms", 500)]
    [InlineData("5s", 5_000)]
    [InlineData("2M", 120_000)]
    public void Durations_parse(string text, int ms)
    {
        Assert.True(FaultInjector.TryParseDuration(text, out var d));
        Assert.Equal(TimeSpan.FromMilliseconds(ms), d);
    }

    [Theory]
    [InlineData("5")]
    [InlineData("-5s")]
    [InlineData("1.5s")]
    [InlineData("9999999999s")]
    [InlineData("s")]
    public void Bad_durations_are_refused(string text) => Assert.False(FaultInjector.TryParseDuration(text, out _));

    public static TheoryData<HermexFaultRule> InvalidRules => new()
    {
        new HermexFaultRule { Stage = HermexSmtpStage.Data },                                        // does nothing
        new HermexFaultRule { Stage = HermexSmtpStage.Data, ReplyCode = 250 },                       // not a failure
        new HermexFaultRule { Stage = HermexSmtpStage.Data, ReplyCode = 451, Drop = true },          // both
        new HermexFaultRule { Stage = HermexSmtpStage.Helo, ReplyCode = 451, Match = "*" },          // Match on wrong stage
        new HermexFaultRule { Stage = HermexSmtpStage.Data, Delay = TimeSpan.FromHours(1) },         // over SessionTimeout
        new HermexFaultRule { Stage = HermexSmtpStage.Data, Drop = true, Probability = 101 },
        new HermexFaultRule { Stage = HermexSmtpStage.Data, Drop = true, EveryNth = 0 },
        new HermexFaultRule { Stage = (HermexSmtpStage)42, Drop = true },
    };

    [Theory]
    [MemberData(nameof(InvalidRules))]
    public void Invalid_rules_fail_validation(HermexFaultRule rule)
    {
        var options = new HermexOptions();
        options.Faults.Rules.Add(rule);
        Assert.Throws<HermexConfigurationException>(options.Validate);
    }

    [Fact]
    public void Enabled_outside_development_refuses_to_start_unless_allowed()
    {
        var production = new StubHostEnvironment { EnvironmentName = "Production" };
        var options = new HermexOptions();
        options.Faults.Enabled = true;
        Assert.Throws<HermexConfigurationException>(() => new FaultInjector(options, production, new CapturingLog()));

        options.Faults.AllowOutsideDevelopment = true;
        Assert.True(new FaultInjector(options, production, new CapturingLog()).Enabled);
    }

    [Fact]
    public void Runtime_enable_is_refused_outside_development_and_when_settings_are_locked()
    {
        var production = new StubHostEnvironment { EnvironmentName = "Production" };
        var injector = new FaultInjector(new HermexOptions(), production, new CapturingLog());
        var refused = injector.Validate(true, null);
        Assert.True(refused is { Forbidden: true });
        Assert.Null(injector.Validate(false, null)); // turning it off is always fine

        var locked = new HermexOptions { AllowRuntimeSettings = false };
        var lockedInjector = new FaultInjector(locked, new StubHostEnvironment(), new CapturingLog());
        Assert.True(lockedInjector.Validate(true, null) is { Forbidden: true });
    }

    [Fact]
    public void Every_nth_fires_deterministically()
    {
        var options = new HermexOptions();
        options.Faults.Enabled = true;
        options.Faults.Rules.Add(new HermexFaultRule { Stage = HermexSmtpStage.Data, ReplyCode = 451, EveryNth = 3 });
        var injector = new FaultInjector(options, new StubHostEnvironment(), new CapturingLog());
        var fired = Enumerable.Range(0, 9).Select(_ => injector.Check(HermexSmtpStage.Data, null) is not null).ToArray();
        Assert.Equal(EveryThird, fired);
    }

    [Fact]
    public void Seeded_probability_is_repeatable()
    {
        bool[] Run()
        {
            var options = new HermexOptions();
            options.Faults.Enabled = true;
            options.Faults.RandomSeed = 7;
            options.Faults.Rules.Add(new HermexFaultRule { Stage = HermexSmtpStage.Data, Drop = true, Probability = 50 });
            var injector = new FaultInjector(options, new StubHostEnvironment(), new CapturingLog());
            return Enumerable.Range(0, 40).Select(_ => injector.Check(HermexSmtpStage.Data, null) is not null).ToArray();
        }
        var first = Run();
        Assert.Equal(first, Run());
        Assert.Contains(true, first);
        Assert.Contains(false, first);
    }

    [Theory]
    [InlineData("X-Hermex-Fault: 451", 451, false, null)]
    [InlineData("x-hermex-fault: drop", null, true, null)]
    [InlineData("X-Hermex-Fault: delay=200ms, 552", 552, false, 200)]
    [InlineData("X-Hermex-Fault: 250", null, false, null)]          // not a failure code: ignored
    [InlineData("X-Hermex-Fault: drop, 451", null, false, null)]    // both: ignored
    [InlineData("X-Hermex-Fault: delay=1h", null, false, null)]     // over SessionTimeout: ignored
    [InlineData("X-Other: 451", null, false, null)]
    [InlineData("X-Hermex-Fault:", null, false, null)]               // empty: ignored, not a no-op fault
    [InlineData("X-Hermex-Fault: ,", null, false, null)]
    [InlineData("X-Hermex-Fault: 451,\n drop", null, false, null)]  // folded: the whole value is invalid
    [InlineData("X-Hermex-Fault: delay=100ms,\n\t451", 451, false, 100)]
    public void Header_trigger_parses(string header, int? code, bool drop, int? delayMs)
    {
        var options = new HermexOptions();
        options.Faults.Enabled = true;
        var injector = new FaultInjector(options, new StubHostEnvironment(), new CapturingLog());
        var fault = injector.FromHeader(TestSupport.Raw($"Subject: x\n{header}\n\nBody\n{header.Replace("Fault", "Fault", StringComparison.Ordinal)}\n"), "s");
        if (code is null && !drop && delayMs is null)
        {
            Assert.Null(fault);
            return;
        }
        Assert.NotNull(fault);
        Assert.Equal(code, fault.ReplyCode);
        Assert.Equal(drop, fault.Drop);
        Assert.Equal(delayMs is null ? null : TimeSpan.FromMilliseconds(delayMs.Value), fault.Delay);
    }

    [Fact]
    public void Infinite_session_timeout_caps_delays_at_the_maximum()
    {
        var options = new HermexOptions { SessionTimeout = Timeout.InfiniteTimeSpan };
        options.Faults.Rules.Add(new HermexFaultRule { Stage = HermexSmtpStage.Helo, Delay = TimeSpan.FromSeconds(5) });
        options.Validate();
        options.Faults.Rules[0].Delay = TimeSpan.FromMinutes(11);
        Assert.Throws<HermexConfigurationException>(options.Validate);
    }

    [Fact]
    public void Header_in_the_body_is_not_a_trigger()
    {
        var options = new HermexOptions();
        options.Faults.Enabled = true;
        var injector = new FaultInjector(options, new StubHostEnvironment(), new CapturingLog());
        Assert.Null(injector.FromHeader(TestSupport.Raw("Subject: x\n\nX-Hermex-Fault: 451\n"), "s"));
    }

    // ------------------------------------------------------------------ over SMTP

    [Fact]
    public async Task Recipient_rule_rejects_matching_addresses_only()
    {
        await using var host = await StartAsync(new HermexFaultRule
            { Stage = HermexSmtpStage.RcptTo, Match = "*@bounce.test", ReplyCode = 550 });
        await using var smtp = await Smtp.ConnectAsync(host);
        await smtp.HelloAsync();
        Assert.StartsWith("250", await smtp.SayAsync("MAIL FROM:<app@example.test>"));
        Assert.StartsWith("550 5.1.1", await smtp.SayAsync("RCPT TO:<gone@bounce.test>"));
        Assert.StartsWith("250", await smtp.SayAsync("RCPT TO:<ann@example.test>"));
    }

    [Fact]
    public async Task Data_fault_means_the_message_is_not_stored()
    {
        await using var host = await StartAsync(new HermexFaultRule
            { Stage = HermexSmtpStage.Data, ReplyCode = 451, EveryNth = 2 });
        await using var smtp = await Smtp.ConnectAsync(host);
        await smtp.HelloAsync();
        Assert.StartsWith("250", await smtp.SendMessageAsync("Subject: one\r\n\r\nBody"));
        Assert.StartsWith("451 4.3.0", await smtp.SendMessageAsync("Subject: two\r\n\r\nBody"));
        Assert.StartsWith("250", await smtp.SendMessageAsync("Subject: three\r\n\r\nBody"));

        var store = host.Services.GetRequiredService<IMailStore>();
        await Until(async () => (await store.GetMessagesAsync(new MessageQuery())).TotalCount == 2);
        await Task.Delay(100);
        var subjects = (await store.GetMessagesAsync(new MessageQuery())).Items.Select(m => m.Subject).Order();
        Assert.Equal(OneAndThree, subjects);
    }

    [Fact]
    public async Task Connect_drop_closes_without_a_greeting()
    {
        await using var host = await StartAsync(new HermexFaultRule { Stage = HermexSmtpStage.Connect, Drop = true });
        await using var smtp = await Smtp.ConnectAsync(host, readGreeting: false);
        Assert.Null(await smtp.ReadReplyAsync());
    }

    [Fact]
    public async Task Reply_421_closes_the_connection()
    {
        await using var host = await StartAsync(new HermexFaultRule { Stage = HermexSmtpStage.MailFrom, ReplyCode = 421 });
        await using var smtp = await Smtp.ConnectAsync(host);
        await smtp.HelloAsync();
        Assert.StartsWith("421", await smtp.SayAsync("MAIL FROM:<app@example.test>"));
        Assert.Null(await smtp.ReadReplyAsync());
    }

    [Fact]
    public async Task Delay_only_slows_the_step_and_then_carries_on()
    {
        await using var host = await StartAsync(new HermexFaultRule
            { Stage = HermexSmtpStage.Helo, Delay = TimeSpan.FromMilliseconds(400) });
        await using var smtp = await Smtp.ConnectAsync(host);
        var watch = Stopwatch.StartNew();
        Assert.StartsWith("250", await smtp.SayAsync("EHLO test"));
        Assert.True(watch.Elapsed >= TimeSpan.FromMilliseconds(350), $"only {watch.Elapsed}");
    }

    [Fact]
    public async Task Header_trigger_rejects_only_that_message_and_logs_no_addresses()
    {
        await using var host = await StartAsync();
        await using var smtp = await Smtp.ConnectAsync(host);
        await smtp.HelloAsync();
        Assert.StartsWith("552", await smtp.SendMessageAsync("X-Hermex-Fault: 552\r\nSubject: big\r\n\r\nBody"));
        Assert.StartsWith("250", await smtp.SendMessageAsync("Subject: fine\r\n\r\nBody"));

        var store = host.Services.GetRequiredService<IMailStore>();
        IReadOnlyList<HermexLogEntry> logs = Array.Empty<HermexLogEntry>();
        await Until(async () => (logs = await store.GetLogsAsync(200)).Any(l => l.Category == "Faults"));
        var entry = logs.First(l => l.Category == "Faults");
        Assert.Contains("552", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("@", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Header_still_applies_after_a_delay_only_rule()
    {
        await using var host = await StartAsync(new HermexFaultRule { Stage = HermexSmtpStage.Data, Delay = TimeSpan.FromMilliseconds(50) });
        await using var smtp = await Smtp.ConnectAsync(host);
        await smtp.HelloAsync();
        Assert.StartsWith("451", await smtp.SendMessageAsync("X-Hermex-Fault: 451\r\n\r\nBody"));
    }

    [Fact]
    public async Task Hook_can_veto_a_change()
    {
        HermexFaultsChangingContext? seen = null;
        await using var host = await StartAsync(configure: o => o.Events.OnFaultsChanging = ctx =>
        {
            seen = ctx;
            if (ctx.Enabled) ctx.Reject("Faults are off on this server.");
            return Task.CompletedTask;
        });
        using var put = await host.Client.PutAsJsonAsync("/hermex/api/faults", new
        {
            enabled = true,
            rules = new object[] { new { stage = "Data", drop = true } },
        });
        Assert.Equal(HttpStatusCode.Forbidden, put.StatusCode);
        Assert.Equal(HermexSmtpStage.Data, Assert.Single(seen!.Rules).Stage);
        Assert.NotNull(seen.HttpContext);
        Assert.Empty(host.Services.GetRequiredService<FaultInjector>().Current.Rules);
    }

    [Fact]
    public async Task Header_trigger_can_be_turned_off_and_does_nothing_while_disabled()
    {
        await using (var host = await StartAsync(configure: o => o.Faults.AllowHeaderTrigger = false))
        await using (var smtp = await Smtp.ConnectAsync(host))
        {
            await smtp.HelloAsync();
            Assert.StartsWith("250", await smtp.SendMessageAsync("X-Hermex-Fault: 552\r\n\r\nBody"));
        }
        await using (var host = await StartAsync(configure: o => o.Faults.Enabled = false))
        await using (var smtp = await Smtp.ConnectAsync(host))
        {
            await smtp.HelloAsync();
            Assert.StartsWith("250", await smtp.SendMessageAsync("X-Hermex-Fault: drop\r\n\r\nBody"));
        }
    }

    [Fact]
    public async Task Rules_can_be_replaced_from_the_api()
    {
        await using var host = await StartAsync(configure: o => o.Faults.Enabled = false);

        var initial = await host.Client.GetFromJsonAsync<FaultStateDto>("/hermex/api/faults");
        Assert.False(initial!.Enabled);
        Assert.True(initial.AllowedHere);

        using var put = await host.Client.PutAsJsonAsync("/hermex/api/faults", new
        {
            enabled = true,
            rules = new object[] { new { stage = "Helo", replyCode = 554, delay = "00:00:00.050" } },
        });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        await using (var smtp = await Smtp.ConnectAsync(host))
            Assert.StartsWith("554", await smtp.SayAsync("EHLO test"));

        using var bad = await host.Client.PutAsJsonAsync("/hermex/api/faults", new
        {
            enabled = true,
            rules = new object[] { new { stage = "Helo", replyCode = 200 } },
        });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Contains("Rule 1", await bad.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Api_refuses_changes_when_runtime_settings_are_locked()
    {
        await using var host = await StartAsync(configure: o => o.AllowRuntimeSettings = false);
        using var put = await host.Client.PutAsJsonAsync("/hermex/api/faults", new { enabled = false });
        Assert.Equal(HttpStatusCode.Forbidden, put.StatusCode);
    }

    // ------------------------------------------------------------------ helpers

    private sealed record FaultStateDto(bool Enabled, bool AllowedHere, bool RuntimeEditable, bool AllowHeaderTrigger);

    private static Task<HermexTestHost> StartAsync(HermexFaultRule? rule = null, Action<HermexOptions>? configure = null) =>
        HermexTestHost.StartAsync(o =>
        {
            o.Faults.Enabled = true;
            if (rule is not null) o.Faults.Rules.Add(rule);
            configure?.Invoke(o);
        });

    private static async Task Until(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Condition not reached in 20 s.");
            await Task.Delay(25);
        }
    }

    private sealed class CapturingLog : IHermexEventLog
    {
        public ConcurrentQueue<string> Messages { get; } = new();

        public void Write(HermexLogLevel level, string category, string message, string? sessionId = null, string? remoteEndPoint = null) =>
            Messages.Enqueue(message);
    }

    /// <summary>A raw SMTP client, so tests see exact replies and dropped connections.</summary>
    private sealed class Smtp : IAsyncDisposable
    {
        private readonly TcpClient _client;
        private readonly StreamReader _reader;
        private readonly StreamWriter _writer;

        private Smtp(TcpClient client)
        {
            _client = client;
            var stream = client.GetStream();
            _reader = new StreamReader(stream, Encoding.ASCII);
            _writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
        }

        public static async Task<Smtp> ConnectAsync(HermexTestHost host, bool readGreeting = true)
        {
            var port = await host.WaitForSmtpPortAsync();
            var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);
            var smtp = new Smtp(client);
            if (readGreeting)
                Assert.StartsWith("220", await smtp.ReadReplyAsync());
            return smtp;
        }

        public async Task HelloAsync() => Assert.StartsWith("250", await SayAsync("EHLO test"));

        public async Task<string?> SayAsync(string line)
        {
            await _writer.WriteLineAsync(line);
            return await ReadReplyAsync();
        }

        /// <summary>The last line of the next reply, or null when the server closed the connection.</summary>
        public async Task<string?> ReadReplyAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            string? line;
            try
            {
                do line = await _reader.ReadLineAsync(timeout.Token);
                while (line is { Length: > 3 } && line[3] == '-');
            }
            catch (IOException)
            {
                return null; // reset by the server
            }
            return line;
        }

        public async Task<string?> SendMessageAsync(string message)
        {
            Assert.StartsWith("250", await SayAsync("MAIL FROM:<app@example.test>"));
            Assert.StartsWith("250", await SayAsync("RCPT TO:<ann@example.test>"));
            Assert.StartsWith("354", await SayAsync("DATA"));
            await _writer.WriteLineAsync(message);
            return await SayAsync(".");
        }

        public ValueTask DisposeAsync()
        {
            _client.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
