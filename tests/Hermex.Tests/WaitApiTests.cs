using System.Net;
using System.Net.Mail;
using System.Reflection;
using Hermex.Storage;
using Hermex.Realtime;
using Hermex.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Hermex.Tests;

/// <summary>ADR-0003 D6 (wait API) and D7 (Hermex.Testing client).</summary>
public sealed class WaitApiTests
{
    private static async Task SendAsync(HermexTestHost host, string to, string subject, string? html = null)
    {
        var port = await host.WaitForSmtpPortAsync();
        using var client = new SmtpClient("127.0.0.1", port);
        using var message = new MailMessage("app@example.test", to, subject, html ?? "Body") { IsBodyHtml = html is not null };
        await client.SendMailAsync(message);
    }

    private const string Long200 =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" +
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public async Task Signal_taken_before_a_pulse_completes()
    {
        var arrivals = new MessageArrivals();
        var next = arrivals.Next;
        Assert.False(next.IsCompleted);
        arrivals.Pulse();
        await next.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(arrivals.Next.IsCompleted); // a fresh signal for the next waiter
    }

    [Fact]
    public async Task Waiter_gets_mail_that_arrives_after_it_started()
    {
        await using var host = await HermexTestHost.StartAsync();
        await host.WaitForSmtpPortAsync();
        var cursor = await new HermexClient(host.Client).MarkAsync(); // explicit cursor: no race with the send
        var wait = host.Client.GetAsync($"/hermex/api/messages/wait?to=ann@&subject=Confirm&after={cursor}&timeout=20");

        await SendAsync(host, "bob@example.test", "Confirm your account"); // wrong recipient: keeps waiting
        await SendAsync(host, "ann@example.test", "Confirm your account");

        using var response = await wait;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("ann@example.test", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Old_mail_never_matches_and_timeout_is_204_with_the_cursor()
    {
        await using var host = await HermexTestHost.StartAsync();
        var old = await host.SeedMessageAsync();

        using var response = await host.Client.GetAsync("/hermex/api/messages/wait?subject=Seeded&timeout=1");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(old.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Assert.Single(response.Headers.GetValues("X-Hermex-After")));

        // An explicit cursor before the old message returns it at once.
        using var explicitAfter = await host.Client.GetAsync("/hermex/api/messages/wait?subject=Seeded&after=0&timeout=1");
        Assert.Equal(HttpStatusCode.OK, explicitAfter.StatusCode);
    }

    [Fact]
    public async Task Message_stored_between_the_check_and_the_wait_is_not_missed()
    {
        // The store stores a matching message right after the first empty check, as if SMTP committed it at
        // that moment. Only a waiter that took the signal before checking sees it before its timeout.
        await using var host = await HermexTestHost.StartAsync(services: s => s.AddSingleton<IMailStore>(sp =>
            RacingStore.Create(ActivatorUtilities.CreateInstance<SqliteMailStore>(sp), sp)));

        var started = DateTime.UtcNow;
        using var response = await host.Client.GetAsync("/hermex/api/messages/wait?subject=Raced&after=0&timeout=30");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10), "the waiter missed the signal and waited out its timeout");
    }

    /// <summary>Forwards to the real store; after the first empty wait check it stores a match and pulses.</summary>
    public class RacingStore : DispatchProxy
    {
        private IMailStore _inner = null!;
        private IServiceProvider _services = null!;
        private static readonly string[] RacedRecipients = { "b@example.test" };
        private int _raced;

        public static IMailStore Create(IMailStore inner, IServiceProvider services)
        {
            var proxy = Create<IMailStore, RacingStore>();
            var racing = (RacingStore)(object)proxy;
            racing._inner = inner;
            racing._services = services;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var result = targetMethod!.Invoke(_inner, args);
            if (targetMethod.Name == nameof(IMailStore.GetMessagesAsync) && args![0] is MessageQuery { AfterId: not null, Search: not null }
                && Interlocked.Exchange(ref _raced, 1) == 0)
                return RaceAsync((Task<PagedResult<MessageSummary>>)result!);
            return result;
        }

        private async Task<PagedResult<MessageSummary>> RaceAsync(Task<PagedResult<MessageSummary>> check)
        {
            var page = await check;
            var raw = TestSupport.Raw("From: a@example.test\nTo: b@example.test\nSubject: Raced\n\nBody\n");
            await _inner.AddMessagesAsync(new[]
            {
                new MessageRecord
                {
                    SessionId = "race", ReceivedAtUtc = DateTimeOffset.UtcNow, EnvelopeFrom = "a@example.test",
                    EnvelopeTo = "b@example.test", FromAddress = "a@example.test", FromDisplay = "a@example.test",
                    ToDisplay = "b@example.test", Subject = "Raced", HasText = true, Recipients = RacedRecipients,
                    RawData = raw, RawSize = raw.Length,
                },
            });
            _services.GetRequiredService<MessageArrivals>().Pulse();
            return page; // still empty: the message arrived after this check
        }
    }

    [Theory]
    [InlineData("timeout=0")]
    [InlineData("timeout=121")]
    [InlineData("after=-1")]
    [InlineData("subject=%22quoted%22")]
    [InlineData("to=a%0Ab")]
    [InlineData("to=" + Long200 + "&from=" + Long200 + "&subject=" + Long200)]  // 3 × 200 + syntax > 512
    public async Task Invalid_requests_are_400(string query)
    {
        await using var host = await HermexTestHost.StartAsync();
        using var response = await host.Client.GetAsync("/hermex/api/messages/wait?" + query);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Waiters_are_capped_and_released_when_clients_leave()
    {
        await using var host = await HermexTestHost.StartAsync();
        var arrivals = host.Services.GetRequiredService<MessageArrivals>();
        using var leave = new CancellationTokenSource();
        using var http = new HttpClient { BaseAddress = host.Client.BaseAddress, Timeout = Timeout.InfiniteTimeSpan };
        var waits = Enumerable.Range(0, MessageArrivals.MaxWaiters)
            .Select(_ => http.GetAsync("/hermex/api/messages/wait?timeout=60", leave.Token)).ToArray();

        await Until(() => arrivals.Waiting == MessageArrivals.MaxWaiters);
        using (var extra = await host.Client.GetAsync("/hermex/api/messages/wait?timeout=1"))
            Assert.Equal(HttpStatusCode.TooManyRequests, extra.StatusCode);

        await leave.CancelAsync();
        await Task.WhenAll(waits.Select(w => w.ContinueWith(_ => { }, TaskScheduler.Default)));
        await Until(() => arrivals.Waiting == 0); // aborted requests give their slots back

        using var again = await host.Client.GetAsync("/hermex/api/messages/wait?timeout=1");
        Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);
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

    // ------------------------------------------------------------------ Hermex.Testing

    [Fact]
    public async Task Client_waits_reads_and_clears()
    {
        await using var host = await HermexTestHost.StartAsync();
        var hermex = new HermexClient(host.Client);
        await host.WaitForSmtpPortAsync();

        var mark = await hermex.MarkAsync();
        await SendAsync(host, "ann@example.test", "Confirm your account",
            "<html><body><a href=\"https://app.test/confirm?t=1\">Confirm</a></body></html>");

        var message = await hermex.WaitForMessageAsync(
            new HermexMessageCriteria { To = "ann@example.test", Subject = "confirm", After = mark }, TimeSpan.FromSeconds(20));
        Assert.Equal("Confirm your account", message.Subject);
        Assert.True(message.Id > mark);

        var detail = await hermex.GetMessageAsync(message.Id);
        Assert.Equal("app@example.test", detail!.FromAddress);
        Assert.NotNull(detail.Header("Subject"));
        Assert.Contains("https://app.test/confirm", await hermex.GetHtmlAsync(message.Id), StringComparison.Ordinal);
        Assert.Equal("https://app.test/confirm?t=1", Assert.Single(await hermex.GetLinksAsync(message.Id)).Href);
        Assert.Null(await hermex.GetMessageAsync(999_999));

        Assert.Equal(1, await hermex.DeleteAllAsync());
        Assert.Equal(0, await hermex.MarkAsync()); // no mail left
    }

    [Fact]
    public async Task Client_keeps_each_request_inside_a_short_HttpClient_timeout()
    {
        await using var host = await HermexTestHost.StartAsync();
        using var http = new HttpClient { BaseAddress = host.Client.BaseAddress, Timeout = TimeSpan.FromSeconds(6) };
        var hermex = new HermexClient(http);

        // 8 s total with a 6 s HttpClient timeout: requests of at most 1 s, ending in the client's own exception.
        await Assert.ThrowsAsync<HermexWaitTimeoutException>(() =>
            hermex.WaitForMessageAsync(new HermexMessageCriteria { Subject = "never" }, TimeSpan.FromSeconds(8)));
    }

    [Fact]
    public async Task Client_throws_its_own_timeout_exception()
    {
        await using var host = await HermexTestHost.StartAsync();
        var hermex = new HermexClient(host.Client);

        var ex = await Assert.ThrowsAsync<HermexWaitTimeoutException>(() =>
            hermex.WaitForMessageAsync(new HermexMessageCriteria { Subject = "never" }, TimeSpan.FromSeconds(1)));
        Assert.Contains("subject contains \"never\"", ex.Message, StringComparison.Ordinal);
    }
}
