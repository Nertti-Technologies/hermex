using System.Net;
using System.Net.Http.Json;
using Hermex.Storage;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Hermex.Tests;

/// <summary>ADR-0003 D10: bulk delete and mark read/unread.</summary>
public sealed class BulkActionTests
{
    [Fact]
    public async Task Bulk_delete_removes_only_the_listed_messages()
    {
        await using var host = await HermexTestHost.StartAsync();
        var a = await host.SeedMessageAsync();
        var b = await host.SeedMessageAsync();
        var keep = await host.SeedMessageAsync();
        var store = host.Services.GetRequiredService<IMailStore>();

        using var response = await host.Client.PostAsJsonAsync("/hermex/api/messages/bulk-delete",
            new { ids = new[] { a, b, 9_999 } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<BulkDeleteResponse>();
        Assert.Equal(new[] { a, b }, body!.Deleted.Order().ToArray());
        Assert.Null(await store.GetMessageAsync(a));
        Assert.NotNull(await store.GetMessageAsync(keep));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"ids\":[]}")]
    [InlineData("")]
    public async Task Empty_or_missing_ids_never_delete_anything(string json)
    {
        await using var host = await HermexTestHost.StartAsync();
        var id = await host.SeedMessageAsync();

        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        using var response = await host.Client.PostAsync("/hermex/api/messages/bulk-delete", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.NotNull(await host.Services.GetRequiredService<IMailStore>().GetMessageAsync(id));
    }

    [Fact]
    public async Task More_than_the_cap_is_refused()
    {
        await using var host = await HermexTestHost.StartAsync();
        var ids = Enumerable.Range(1, 501).Select(i => (long)i).ToArray();

        using var response = await host.Client.PostAsJsonAsync("/hermex/api/messages/bulk-delete", new { ids });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Bulk_read_marks_read_and_unread()
    {
        await using var host = await HermexTestHost.StartAsync();
        var a = await host.SeedMessageAsync();
        var b = await host.SeedMessageAsync();
        var store = host.Services.GetRequiredService<IMailStore>();

        using (var read = await host.Client.PostAsJsonAsync("/hermex/api/messages/bulk-read", new { ids = new[] { a, b } }))
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal(0, (await store.GetStatsAsync()).UnreadMessages);

        using (var unread = await host.Client.PostAsJsonAsync("/hermex/api/messages/bulk-read",
                   new { ids = new[] { a }, isRead = false }))
            Assert.Equal(HttpStatusCode.OK, unread.StatusCode);
        Assert.Equal(1, (await store.GetStatsAsync()).UnreadMessages);
    }

    [Fact]
    public async Task Deleting_hook_receives_the_ids_and_can_veto()
    {
        IReadOnlyList<long>? seen = null;
        HermexDeleteScope? scope = null;
        await using var host = await HermexTestHost.StartAsync(o => o.Events.OnDeleting = ctx =>
        {
            seen = ctx.MessageIds;
            scope = ctx.Scope;
            ctx.Reject("Kept for audit.");
            return Task.CompletedTask;
        });
        var id = await host.SeedMessageAsync();

        using var response = await host.Client.PostAsJsonAsync("/hermex/api/messages/bulk-delete", new { ids = new[] { id } });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(HermexDeleteScope.Messages, scope);
        Assert.Equal(new[] { id }, seen);
        Assert.NotNull(await host.Services.GetRequiredService<IMailStore>().GetMessageAsync(id));
    }

    private sealed record BulkDeleteResponse(long[] Deleted);
}
