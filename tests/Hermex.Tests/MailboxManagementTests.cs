using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using Hermex.Diagnostics;
using Hermex.Storage;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Hermex.Tests;

/// <summary>ADR-0003 D1 (delete a mailbox), D2 (reset), D3 (IMAP view of both) and D12 (delete scope).</summary>
public sealed class MailboxManagementTests
{
    private static MessageRecord Record(string subject, params string[] recipients)
    {
        var raw = TestSupport.Raw($"From: sender@example.test\nTo: {string.Join(", ", recipients)}\nSubject: {subject}\n\nBody\n");
        return new MessageRecord
        {
            SessionId = "test",
            ReceivedAtUtc = DateTimeOffset.UtcNow,
            EnvelopeFrom = "sender@example.test",
            EnvelopeTo = string.Join(", ", recipients),
            FromAddress = "sender@example.test",
            FromDisplay = "sender@example.test",
            ToDisplay = string.Join(", ", recipients),
            Subject = subject,
            HasText = true,
            Recipients = recipients,
            RawData = raw,
            RawSize = raw.Length,
        };
    }

    private static async Task<long> MailboxIdAsync(IMailStore store, string address) =>
        (await store.GetMailboxAsync(address))!.Id;

    // ------------------------------------------------------------------ store

    [Fact]
    public async Task Deleting_a_mailbox_keeps_messages_other_mailboxes_hold()
    {
        await using var context = await TestStore.CreateAsync();
        var store = context.Store;
        var exclusive = Record("Only A", "a@example.test");
        var shared = Record("A and B", "a@example.test", "b@example.test");
        var other = Record("Only B", "b@example.test");
        await store.AddMessagesAsync(new[] { exclusive, shared, other });

        var result = await store.DeleteMailboxAsync(await MailboxIdAsync(store, "a@example.test"));

        Assert.NotNull(result);
        Assert.Equal("a@example.test", result!.Address);
        Assert.Equal(new[] { exclusive.Id }, result.DeletedMessageIds);
        Assert.Null(await store.GetMessageAsync(exclusive.Id));
        Assert.NotNull(await store.GetMessageAsync(shared.Id));
        Assert.Null(await store.GetMailboxAsync("a@example.test"));

        var b = await store.GetMailboxAsync("b@example.test");
        Assert.Equal(2, b!.MessageCount);
        var inbox = await store.GetMailboxAsync(SqliteMailStore.InboxMailboxName);
        Assert.Equal(2, inbox!.MessageCount);
    }

    [Fact]
    public async Task Inbox_cannot_be_deleted_and_a_missing_mailbox_returns_null()
    {
        await using var context = await TestStore.CreateAsync();
        var inboxId = await MailboxIdAsync(context.Store, SqliteMailStore.InboxMailboxName);

        await Assert.ThrowsAsync<InvalidOperationException>(() => context.Store.DeleteMailboxAsync(inboxId));
        Assert.Null(await context.Store.DeleteMailboxAsync(9_999));
    }

    [Fact]
    public async Task Reset_removes_messages_mailboxes_and_logs_and_keeps_settings_and_uids_rising()
    {
        await using var context = await TestStore.CreateAsync();
        var store = context.Store;
        var first = Record("Before", "a@example.test");
        await store.AddMessagesAsync(new[] { first });
        await store.AddLogsAsync(new[] { new HermexLogEntry { Category = "Test", Message = "entry" } });
        await store.SaveSettingsAsync(new Dictionary<string, string> { ["key"] = "value" });
        var uidNextBefore = (await store.GetMailboxAsync(SqliteMailStore.InboxMailboxName))!.UidNext;
        var uidValidityBefore = (await store.GetMailboxAsync(SqliteMailStore.InboxMailboxName))!.UidValidity;

        var result = await store.ResetAsync();

        Assert.Equal(1, result.MessagesDeleted);
        Assert.Equal(1, result.MailboxesDeleted);
        Assert.Equal(1, result.LogsDeleted);
        Assert.Equal("value", (await store.GetSettingsAsync())["key"]);
        var mailboxes = await store.GetMailboxesAsync();
        var inbox = Assert.Single(mailboxes);
        Assert.Equal(SqliteMailStore.InboxMailboxName, inbox.Address);

        // UIDs are never reused (RFC 9051 §2.3.1.1), so UIDNEXT never goes back and UIDVALIDITY can stay.
        Assert.Equal(uidValidityBefore, inbox.UidValidity);
        Assert.Equal(uidNextBefore, inbox.UidNext);
        var after = Record("After", "a@example.test");
        await store.AddMessagesAsync(new[] { after });
        Assert.True(after.Id > first.Id);
    }

    // ------------------------------------------------------------------ API

    [Fact]
    public async Task Reset_requires_the_typed_confirmation()
    {
        await using var host = await HermexTestHost.StartAsync();
        var id = await host.SeedMessageAsync();
        var store = host.Services.GetRequiredService<IMailStore>();

        using var noBody = await host.Client.PostAsync("/hermex/api/reset", null);
        using var wrong = await host.Client.PostAsJsonAsync("/hermex/api/reset", new { confirm = "reset" });
        using var garbage = await host.Client.PostAsync("/hermex/api/reset",
            new StringContent("{not json", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, noBody.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, garbage.StatusCode);
        Assert.NotNull(await store.GetMessageAsync(id));

        using var ok = await host.Client.PostAsJsonAsync("/hermex/api/reset", new { confirm = "RESET" });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Null(await store.GetMessageAsync(id));
    }

    [Fact]
    public async Task Mailbox_delete_api_refuses_inbox_and_reports_missing()
    {
        await using var host = await HermexTestHost.StartAsync();
        await host.SeedMessageAsync();
        var store = host.Services.GetRequiredService<IMailStore>();
        var inboxId = await MailboxIdAsync(store, SqliteMailStore.InboxMailboxName);
        var recipientId = await MailboxIdAsync(store, "recipient@example.test");

        using var inbox = await host.Client.DeleteAsync($"/hermex/api/mailboxes/{inboxId}");
        using var missing = await host.Client.DeleteAsync("/hermex/api/mailboxes/9999");
        using var ok = await host.Client.DeleteAsync($"/hermex/api/mailboxes/{recipientId}");

        Assert.Equal(HttpStatusCode.BadRequest, inbox.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Null(await store.GetMailboxAsync("recipient@example.test"));
    }

    [Fact]
    public async Task Deleting_hook_sees_the_scope_and_can_veto_mailbox_delete_and_reset()
    {
        var seen = new List<(HermexDeleteScope Scope, string? Mailbox)>();
        await using var host = await HermexTestHost.StartAsync(o => o.Events.OnDeleting = ctx =>
        {
            seen.Add((ctx.Scope, ctx.MailboxAddress));
            ctx.Reject("Shared server.");
            return Task.CompletedTask;
        });
        var id = await host.SeedMessageAsync();
        var store = host.Services.GetRequiredService<IMailStore>();
        var mailboxId = await MailboxIdAsync(store, "recipient@example.test");

        using var mailbox = await host.Client.DeleteAsync($"/hermex/api/mailboxes/{mailboxId}");
        using var reset = await host.Client.PostAsJsonAsync("/hermex/api/reset", new { confirm = "RESET" });
        using var all = await host.Client.DeleteAsync("/hermex/api/messages");

        Assert.Equal(HttpStatusCode.Forbidden, mailbox.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, reset.StatusCode);
        Assert.Equal(
            new[] { (HermexDeleteScope.Mailbox, (string?)"recipient@example.test"), (HermexDeleteScope.Reset, null), (HermexDeleteScope.All, null) },
            seen.ToArray());
        Assert.NotNull(await store.GetMessageAsync(id));
    }

    // ------------------------------------------------------------------ IMAP

    [Fact]
    public async Task Imap_session_gets_expunge_for_external_delete_and_bye_when_its_mailbox_is_deleted()
    {
        var imapPort = HermexTestHost.FreePort();
        await using var host = await HermexTestHost.StartAsync(o => { o.EnableImap = true; o.ImapPort = imapPort; });
        var store = host.Services.GetRequiredService<IMailStore>();
        var first = Record("One", "box@example.test");
        var second = Record("Two", "box@example.test");
        await store.AddMessagesAsync(new[] { first, second });
        var port = await WaitForImapPortAsync(host);

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII);
        await using var writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };

        await reader.ReadLineAsync(); // greeting
        await CommandAsync(writer, reader, "a1 LOGIN user pass", "a1");
        var select = await CommandAsync(writer, reader, "a2 SELECT box@example.test", "a2");
        Assert.Contains("* 2 EXISTS", select);

        // The first message (sequence 1) is deleted from the dashboard.
        using (var delete = await host.Client.DeleteAsync($"/hermex/api/messages/{first.Id}"))
            Assert.Equal(HttpStatusCode.OK, delete.StatusCode);
        var noop = await CommandAsync(writer, reader, "a3 NOOP", "a3");
        Assert.Contains("* 1 EXPUNGE", noop);

        // New mail is announced with EXISTS even during STORE (EXPUNGE would not be allowed there).
        await store.AddMessagesAsync(new[] { Record("Three", "box@example.test") });
        var storeResponse = await CommandAsync(writer, reader, "a3b STORE 1 +FLAGS (\\Seen)", "a3b");
        Assert.Contains("* 2 EXISTS", storeResponse);

        // The mailbox itself is deleted: the session is closed with BYE.
        var mailboxId = await MailboxIdAsync(store, "box@example.test");
        using (var deleteMailbox = await host.Client.DeleteAsync($"/hermex/api/mailboxes/{mailboxId}"))
            Assert.Equal(HttpStatusCode.OK, deleteMailbox.StatusCode);
        await writer.WriteLineAsync("a4 NOOP");
        var line = await reader.ReadLineAsync();
        Assert.StartsWith("* BYE", line);
    }

    private static async Task<List<string>> CommandAsync(StreamWriter writer, StreamReader reader, string command, string tag)
    {
        await writer.WriteLineAsync(command);
        var lines = new List<string>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            var line = await reader.ReadLineAsync(timeout.Token) ?? throw new EndOfStreamException();
            lines.Add(line);
            if (line.StartsWith(tag + " ", StringComparison.Ordinal))
                return lines;
        }
    }

    private static async Task<int> WaitForImapPortAsync(HermexTestHost host)
    {
        var state = host.Services.GetRequiredService<HermexRuntimeState>();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (state.ImapStatus == HermexServerStatus.Listening && state.ImapListeningPort is { } port)
                return port;
            await Task.Delay(25);
        }
        throw new TimeoutException("The IMAP listener did not start.");
    }
}
