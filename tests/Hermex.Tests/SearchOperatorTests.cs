using Hermex.Storage;
using Xunit;

namespace Hermex.Tests;

/// <summary>ADR-0003 D9: search operators, run through the store against real SQLite.</summary>
public sealed class SearchOperatorTests : IAsyncLifetime
{
    private TestStore _context = null!;
    private readonly Dictionary<string, long> _ids = new();

    public async Task InitializeAsync()
    {
        _context = await TestStore.CreateAsync();
        await AddAsync("invoice", "Invoice 2026", from: "billing@shop.test", to: "alice@example.test", cc: "audit@example.test",
            size: 2_000, attachments: 1, html: true, received: new DateTimeOffset(2026, 11, 20, 23, 30, 0, TimeSpan.Zero));
        await AddAsync("newsletter", "Weekly 100% off_sale", from: "news@shop.test", to: "bob@example.test", cc: null,
            size: 50_000, attachments: 0, html: true, received: new DateTimeOffset(2026, 11, 21, 8, 0, 0, TimeSpan.Zero));
        await AddAsync("reset", "Reset your password", from: "auth@app.test", to: "alice@example.test", cc: null,
            size: 900, attachments: 0, html: false, received: new DateTimeOffset(2026, 11, 21, 9, 0, 0, TimeSpan.Zero));
        await _context.Store.SetReadAsync(_ids["reset"], true);
    }

    public async Task DisposeAsync() => await _context.DisposeAsync();

    private async Task AddAsync(string key, string subject, string from, string to, string? cc, int size, int attachments,
        bool html, DateTimeOffset received)
    {
        var raw = TestSupport.Raw($"From: {from}\nTo: {to}\nSubject: {subject}\n\nBody\n");
        var record = new MessageRecord
        {
            SessionId = "search",
            ReceivedAtUtc = received,
            EnvelopeFrom = from,
            EnvelopeTo = to,
            FromAddress = from,
            FromDisplay = from,
            ToDisplay = to,
            CcDisplay = cc,
            Subject = subject,
            HasHtml = html,
            HasText = true,
            RawSize = size,
            AttachmentCount = attachments,
            Recipients = new[] { to },
            RawData = raw,
        };
        await _context.Store.AddMessagesAsync(new[] { record });
        _ids[key] = record.Id;
    }

    private async Task<string[]> SearchAsync(string query)
    {
        var page = await _context.Store.GetMessagesAsync(new MessageQuery { Search = query, PageSize = 50 });
        return page.Items.Select(i => _ids.Single(kv => kv.Value == i.Id).Key).Order().ToArray();
    }

    [Theory]
    [InlineData("invoice", new[] { "invoice" })]
    [InlineData("from:shop.test", new[] { "invoice", "newsletter" })]
    [InlineData("to:alice", new[] { "invoice", "reset" })]
    [InlineData("cc:audit", new[] { "invoice" })]
    [InlineData("-cc:audit", new[] { "newsletter", "reset" })]
    [InlineData("subject:\"your password\"", new[] { "reset" })]
    [InlineData("\"Weekly 100\"", new[] { "newsletter" })]
    [InlineData("is:read", new[] { "reset" })]
    [InlineData("is:unread", new[] { "invoice", "newsletter" })]
    [InlineData("has:attachment", new[] { "invoice" })]
    [InlineData("-has:attachment", new[] { "newsletter", "reset" })]
    [InlineData("has:html -from:news", new[] { "invoice" })]
    [InlineData("larger:10kb", new[] { "newsletter" })]
    [InlineData("smaller:1k", new[] { "reset" })]
    [InlineData("larger:1.5kb", new[] { "invoice", "newsletter" })]
    [InlineData("cc:audit invoice", new[] { "invoice" })]
    [InlineData("audit", new[] { "invoice" })]                      // bare terms include Cc
    [InlineData("-larger:10kb", new[] { "invoice", "reset" })]
    [InlineData("after:2026-11-21", new[] { "newsletter", "reset" })]
    [InlineData("before:2026/11/21", new[] { "invoice" })]
    [InlineData("mailbox:bob@", new[] { "newsletter" })]
    [InlineData("to:alice is:unread", new[] { "invoice" })]
    public async Task Operators_filter_as_documented(string query, string[] expected) =>
        Assert.Equal(expected, await SearchAsync(query));

    [Theory]
    [InlineData("100%", new[] { "newsletter" })]      // % is literal, not a wildcard
    [InlineData("off_sale", new[] { "newsletter" })]  // _ is literal
    [InlineData("o_f", new string[0])]                // would match "off" if _ were a wildcard
    [InlineData("%", new[] { "newsletter" })]
    public async Task Like_wildcards_in_input_are_literal(string query, string[] expected) =>
        Assert.Equal(expected, await SearchAsync(query));

    [Theory]
    [InlineData("larger:abc")]          // bad size: searched as text, matches nothing
    [InlineData("after:yesterday")]     // bad date
    [InlineData("color:red")]           // unknown operator
    [InlineData("larger:9999999999999999mb")]     // fits a long, but bytes would overflow: refused, not wrapped
    public async Task Unreadable_operators_are_plain_text(string query) =>
        Assert.Empty(await SearchAsync(query));

    [Fact]
    public async Task Injection_attempts_stay_parameters()
    {
        Assert.Empty(await SearchAsync("subject:\"x') OR 1=1 --\""));
        Assert.Empty(await SearchAsync("'; DROP TABLE Messages; --"));
        Assert.Equal(3, (await _context.Store.GetMessagesAsync(new MessageQuery())).TotalCount);
    }

    [Fact]
    public async Task Overlong_query_is_bounded()
    {
        var query = string.Join(' ', Enumerable.Repeat("invoice", 200));
        Assert.Equal(InvoiceOnly, await SearchAsync(query));
    }

    private static readonly string[] InvoiceOnly = { "invoice" };

    [Theory]
    [InlineData("a b", "a|b")]
    [InlineData("\"a b\" c", "a b|c")]
    [InlineData("-x", "-x")]
    [InlineData("- x", "-|x")]                 // a lone '-' is a word, not negation
    [InlineData("from:\"Ann Lee\"", "from=Ann Lee")]
    [InlineData("subject:\"open", "subject=open")] // unclosed quote runs to the end
    [InlineData("from:", "from:")]              // operator without value: plain text
    [InlineData(":x", ":x")]
    [InlineData("from:\"\"", "")]             // empty value: dropped
    [InlineData("A:b", "a=b")]                  // operator names are case-insensitive
    public void Tokenizer_handles_edge_cases(string query, string expected)
    {
        var tokens = MessageSearch.Tokenize(query).Select(t =>
            (t.Negated ? "-" : "") + (t.Operator is null ? t.Value : t.Operator + "=" + t.Value));
        Assert.Equal(expected, string.Join("|", tokens));
    }

    [Theory]
    [InlineData("10", 10L)]
    [InlineData("10kb", 10_240L)]
    [InlineData("1.5mb", 1_572_864L)]
    [InlineData("2K", 2_048L)]
    public void Sizes_parse_with_units(string value, long bytes)
    {
        Assert.True(MessageSearch.TryParseSize(value, out var parsed));
        Assert.Equal(bytes, parsed);
    }

    [Theory]
    [InlineData("-5")]
    [InlineData("1e3")]
    [InlineData("9999999999999999mb")]
    [InlineData("kb")]
    public void Bad_sizes_are_refused(string value) => Assert.False(MessageSearch.TryParseSize(value, out _));
}

/// <summary>ADR-0003 D9 / CLAUDE.md §8: rows stored before 2.3.0 get CcDisplay in the migration that adds it.</summary>
public sealed class CcBackfillTests
{
    [Fact]
    public async Task Existing_rows_are_backfilled_once_on_upgrade()
    {
        var directory = Path.Combine(Path.GetTempPath(), "hermex-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var dbPath = Path.Combine(directory, "test.db");
        try
        {
            long id;
            await using (var first = await TestStore.CreateAsync(o => o.DatabasePath = dbPath, deleteOnDispose: false))
            {
                var raw = TestSupport.Raw("From: a@x.test\nTo: b@x.test\nCc: Audit Team <audit@x.test>\nSubject: Old\n\nBody\n");
                var record = new MessageRecord
                {
                    SessionId = "old", ReceivedAtUtc = DateTimeOffset.UtcNow, EnvelopeFrom = "a@x.test", EnvelopeTo = "b@x.test",
                    FromAddress = "a@x.test", FromDisplay = "a@x.test", ToDisplay = "b@x.test", Subject = "Old", HasText = true,
                    Recipients = new[] { "b@x.test" }, RawData = raw, RawSize = raw.Length,
                };
                await first.Store.AddMessagesAsync(new[] { record });
                id = record.Id;
            }

            // Make the database look like 2.2: Cc unknown, data version 0.
            await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "UPDATE Messages SET CcDisplay = NULL; PRAGMA user_version = 0;";
                await command.ExecuteNonQueryAsync();
            }
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

            await using var second = await TestStore.CreateAsync(o => o.DatabasePath = dbPath, deleteOnDispose: false);
            var found = await second.Store.GetMessagesAsync(new MessageQuery { Search = "cc:audit" });
            Assert.Equal(id, Assert.Single(found.Items).Id);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(directory, recursive: true); } catch { /* best effort */ }
        }
    }
}
