using System.Collections.Concurrent;
using System.Net;
using System.Net.Mail;
using System.Text.Json;
using Hermex.Smtp;
using Hermex.Storage;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Hermex.Tests;

/// <summary>ADR-0002 D4 with amendments A4, A6 and A9.</summary>
public sealed class EventHookTests
{
    private static HttpRequestMessage Delete(string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, path);
        request.Headers.Add("X-Hermex-Request", "1");
        return request;
    }

    private static async Task<bool> WaitForAsync(Func<Task<bool>> condition)
    {
        // Persistence flushes every 20 ms in the test host; 5 s is ample headroom.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
                return true;
            await Task.Delay(25);
        }
        return false;
    }

    private static async Task SendMailAsync(HermexTestHost host, string subject)
    {
        var port = host.Services.GetRequiredService<HermexRuntimeState>().ListeningPort
                   ?? throw new InvalidOperationException("SMTP is not listening.");
        using var client = new SmtpClient("127.0.0.1", port);
        using var message = new MailMessage("sender@example.test", "recipient@example.test", subject, "Body");
        await client.SendMailAsync(message);
    }

    [Fact]
    public async Task Settings_changing_reject_stops_the_change()
    {
        await using var host = await HermexTestHost.StartAsync(o =>
            o.Events.OnSettingsChanging = ctx => { ctx.Reject("Frozen for the release."); return Task.CompletedTask; });
        var settings = host.Services.GetRequiredService<IHermexSettings>();
        var proposed = settings.GetCurrent();
        proposed.DashboardTitle = "Changed";

        var result = await settings.UpdateAsync(proposed);

        Assert.Equal(HermexSettingsUpdateStatus.Rejected, result.Status);
        Assert.Equal("Frozen for the release.", result.Error);
        Assert.Equal("Hermex", host.Options.DashboardTitle);

        using var response = await host.Client.PutAsync("/hermex/api/settings",
            new StringContent(JsonSerializer.Serialize(proposed), System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Settings_changing_exception_rejects()
    {
        await using var host = await HermexTestHost.StartAsync(o =>
            o.Events.OnSettingsChanging = _ => throw new InvalidOperationException("boom"));
        var settings = host.Services.GetRequiredService<IHermexSettings>();

        var result = await settings.UpdateAsync(settings.GetCurrent());

        Assert.Equal(HermexSettingsUpdateStatus.Rejected, result.Status);
    }

    [Fact]
    public async Task Settings_changing_can_adjust_and_is_validated_again()
    {
        await using var host = await HermexTestHost.StartAsync(o =>
            o.Events.OnSettingsChanging = ctx =>
            {
                ctx.Proposed.RelayEnabled = false;                       // force relay off
                if (ctx.Proposed.DashboardTitle == "break")
                    ctx.Proposed.ServerHostName = "";                    // invalid after the hook
                return Task.CompletedTask;
            });
        var settings = host.Services.GetRequiredService<IHermexSettings>();
        var proposed = settings.GetCurrent();
        proposed.RelayEnabled = true;

        Assert.Equal(HermexSettingsUpdateStatus.Saved, (await settings.UpdateAsync(proposed)).Status);
        Assert.False(host.Options.Relay.Enabled);
        Assert.True(proposed.RelayEnabled); // the caller's object is not mutated

        proposed.DashboardTitle = "break";
        Assert.Equal(HermexSettingsUpdateStatus.Invalid, (await settings.UpdateAsync(proposed)).Status);
    }

    [Fact]
    public async Task Settings_hooks_see_persisted_source_at_startup()
    {
        var sources = new ConcurrentBag<HermexSettingsChangeSource>();
        var changed = new ConcurrentBag<HermexSettingsChangeSource>();
        await using var host = await HermexTestHost.StartAsync(
            o =>
            {
                o.Events.OnSettingsChanging = ctx => { sources.Add(ctx.Source); return Task.CompletedTask; };
                o.Events.OnSettingsChanged = ctx => { changed.Add(ctx.Source); return Task.CompletedTask; };
            },
            beforeUseMail4Dev: async app =>
            {
                var store = app.Services.GetRequiredService<IMailStore>();
                await store.InitializeAsync();
                await store.SaveSettingsAsync(new Dictionary<string, string>
                {
                    ["hermex.settings"] = JsonSerializer.Serialize(new HermexSettings { DashboardTitle = "Stored" }),
                });
            });

        Assert.Equal(new[] { HermexSettingsChangeSource.Persisted }, sources.ToArray());
        Assert.Equal(new[] { HermexSettingsChangeSource.Persisted }, changed.ToArray());
    }

    [Fact]
    public async Task Deleting_reject_keeps_the_message()
    {
        await using var host = await HermexTestHost.StartAsync(o =>
            o.Events.OnDeleting = ctx => { ctx.Reject("Shared inbox."); return Task.CompletedTask; });
        var id = await host.SeedMessageAsync();

        using var one = await host.Client.SendAsync(Delete($"/hermex/api/messages/{id}"));
        using var all = await host.Client.SendAsync(Delete("/hermex/api/messages"));

        Assert.Equal(HttpStatusCode.Forbidden, one.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, all.StatusCode);
        Assert.NotNull(await host.Services.GetRequiredService<IMailStore>().GetMessageAsync(id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Relaying_reject_or_exception_stops_the_relay(bool throws)
    {
        HermexRelayingContext? seen = null;
        await using var host = await HermexTestHost.StartAsync(o =>
        {
            o.Relay.Enabled = true;
            o.Relay.Host = "smtp.example.test";
            o.Events.OnRelaying = ctx =>
            {
                seen = ctx;
                if (throws)
                    throw new InvalidOperationException("boom");
                ctx.Reject("Test mail is never forwarded.");
                return Task.CompletedTask;
            };
        });
        var id = await host.SeedMessageAsync();

        var result = await host.Services.GetRequiredService<IMailRelayService>().RelayAsync(id);

        Assert.False(result.Success);
        Assert.NotNull(seen);
        Assert.Equal(id, seen!.MessageId);
        Assert.Equal("smtp.example.test", seen.Host);
        Assert.False(seen.Automatic);
        if (!throws)
            Assert.Equal("Test mail is never forwarded.", result.Error);
    }

    [Fact]
    public async Task Relay_veto_is_a_403_from_the_api()
    {
        await using var host = await HermexTestHost.StartAsync(o =>
        {
            o.Relay.Enabled = true;
            o.Relay.Host = "smtp.example.test";
            o.Events.OnRelaying = ctx => { ctx.Reject("No."); return Task.CompletedTask; };
        });
        var id = await host.SeedMessageAsync();

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/hermex/api/messages/{id}/relay");
        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Automatic_relay_passes_through_the_hook()
    {
        var automatic = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = await HermexTestHost.StartAsync(o =>
        {
            o.Relay.Enabled = true;
            o.Relay.AutomaticRelay = true;
            o.Relay.Host = "smtp.example.test";
            o.Events.OnRelaying = ctx =>
            {
                automatic.TrySetResult(ctx.Automatic);
                ctx.Reject("No.");
                return Task.CompletedTask;
            };
        });

        await SendMailAsync(host, "Automatic");

        Assert.True(await automatic.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Message_stored_is_notified_and_a_failing_hook_does_not_stop_capture()
    {
        var subjects = new ConcurrentQueue<string>();
        await using var host = await HermexTestHost.StartAsync(o =>
            o.Events.OnMessageStored = ctx =>
            {
                subjects.Enqueue(ctx.Subject);
                throw new InvalidOperationException("audit store down");
            });

        await SendMailAsync(host, "First");
        await SendMailAsync(host, "Second");

        Assert.True(await WaitForAsync(() => Task.FromResult(subjects.Count == 2)));
        Assert.Equal(new[] { "First", "Second" }, subjects.ToArray());
        var stats = await host.Services.GetRequiredService<IMailStore>().GetStatsAsync();
        Assert.Equal(2, stats.TotalMessages);
    }

    [Fact]
    public async Task Registered_events_subclass_replaces_options_events_in_either_order()
    {
        var hook = new RejectAllDeletes();
        await using var host = await HermexTestHost.StartAsync(
            o => o.Events.OnDeleting = _ => Task.CompletedTask,
            services => services.AddSingleton<HermexEvents>(hook)); // registered after AddMail4Dev
        var id = await host.SeedMessageAsync();

        using var response = await host.Client.SendAsync(Delete($"/hermex/api/messages/{id}"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(1, hook.Calls);
    }

    private sealed class RejectAllDeletes : HermexEvents
    {
        public int Calls;

        public override Task Deleting(HermexDeletingContext context)
        {
            Interlocked.Increment(ref Calls);
            context.Reject("Never.");
            return Task.CompletedTask;
        }
    }
}
