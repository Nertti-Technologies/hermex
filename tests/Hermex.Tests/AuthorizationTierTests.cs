using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Hermex.Realtime;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Connections.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Hermex.Tests;

/// <summary>ADR-0002 D3 with amendments A1, A2, A5, A7 and A11.</summary>
public sealed class AuthorizationTierTests
{
    private const string ViewerRoles = "";              // signed in, no roles
    private const string ManagerRoles = "manager";
    private const string AdminRoles = "manager,admin";

    private static Task<HermexTestHost> StartWithPoliciesAsync(Action<HermexOptions>? extra = null) =>
        HermexTestHost.StartAsync(
            o =>
            {
                o.Authorization.ViewerPolicy = "SignedIn";
                o.Authorization.ManagerPolicy = "Manage";
                o.Authorization.AdminPolicy = "Admin";
                extra?.Invoke(o);
            },
            TestAuthHandler.AddTo);

    private static async Task<HttpStatusCode> SendAsync(HermexTestHost host, HttpMethod method, string path,
        string? roles, bool requestHeader = true)
    {
        using var request = new HttpRequestMessage(method, path);
        if (roles is not null)
            request.Headers.Add(TestAuthHandler.RolesHeader, roles);
        if (requestHeader)
            request.Headers.Add("X-Hermex-Request", "1");
        if (method == HttpMethod.Put)
            request.Content = JsonContent(new HermexSettings { SmtpPort = 0 }); // invalid: 400 once authorized, no re-bind
        using var response = await host.Client.SendAsync(request);
        return response.StatusCode;
    }

    private static HttpContent JsonContent(object value) =>
        new StringContent(JsonSerializer.Serialize(value), System.Text.Encoding.UTF8, "application/json");

    public static TheoryData<string, string, HermexEndpointTier> Endpoints => new()
    {
        { "GET", "/hermex", HermexEndpointTier.Viewer },
        { "GET", "/hermex/api/messages", HermexEndpointTier.Viewer },
        { "GET", "/hermex/api/mailboxes", HermexEndpointTier.Viewer },
        { "GET", "/hermex/api/messages/{id}", HermexEndpointTier.Viewer },
        { "GET", "/hermex/api/messages/{id}/html", HermexEndpointTier.Viewer },
        { "GET", "/hermex/api/messages/{id}/raw", HermexEndpointTier.Viewer },
        { "GET", "/hermex/api/messages/{id}/attachments/0", HermexEndpointTier.Viewer },
        { "POST", "/hermex/api/messages/{id}/read", HermexEndpointTier.Viewer },
        { "GET", "/hermex/api/status", HermexEndpointTier.Viewer },
        { "POST", "/hermex/hub/negotiate?negotiateVersion=1", HermexEndpointTier.Viewer },
        { "POST", "/hermex/api/messages/{id}/relay", HermexEndpointTier.Manager },
        { "DELETE", "/hermex/api/messages/{id}", HermexEndpointTier.Manager },
        { "DELETE", "/hermex/api/messages", HermexEndpointTier.Manager },
        { "GET", "/hermex/settings", HermexEndpointTier.Admin },
        { "GET", "/hermex/logs", HermexEndpointTier.Admin },
        { "GET", "/hermex/api/settings", HermexEndpointTier.Admin },
        { "PUT", "/hermex/api/settings", HermexEndpointTier.Admin },
        { "GET", "/hermex/api/logs", HermexEndpointTier.Admin },
        { "DELETE", "/hermex/api/logs", HermexEndpointTier.Admin },
    };

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task Each_endpoint_requires_its_tier(string method, string path, HermexEndpointTier tier)
    {
        await using var host = await StartWithPoliciesAsync();
        var id = await host.SeedMessageAsync();
        var url = path.Replace("{id}", id.ToString());
        var verb = new HttpMethod(method);

        Assert.Equal(HttpStatusCode.Unauthorized, await SendAsync(host, verb, url, roles: null));

        var lowerRoles = tier switch
        {
            HermexEndpointTier.Admin => ManagerRoles,
            HermexEndpointTier.Manager => ViewerRoles,
            _ => null,
        };
        if (lowerRoles is not null)
            Assert.Equal(HttpStatusCode.Forbidden, await SendAsync(host, verb, url, lowerRoles));

        var allowed = await SendAsync(host, verb, url, AdminRoles);
        Assert.NotEqual(HttpStatusCode.Unauthorized, allowed);
        Assert.NotEqual(HttpStatusCode.Forbidden, allowed);
    }

    [Fact]
    public async Task Every_hermex_endpoint_carries_tier_metadata()
    {
        await using var host = await StartWithPoliciesAsync();
        // Build the endpoint list (Razor Pages are materialised lazily).
        await host.Client.GetAsync("/hermex/api/status");

        var endpoints = host.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.TrimStart('/').StartsWith("hermex", StringComparison.OrdinalIgnoreCase) == true)
            .Where(e => e.RoutePattern.RawText?.Contains("/assets/", StringComparison.Ordinal) != true)
            .ToList();

        Assert.NotEmpty(endpoints);
        Assert.All(endpoints, e => Assert.NotNull(e.Metadata.GetMetadata<HermexEndpointMetadata>()));
    }

    [Fact]
    public async Task Null_higher_tiers_inherit_the_viewer_policy()
    {
        await using var host = await HermexTestHost.StartAsync(
            o => o.Authorization.ViewerPolicy = "SignedIn", TestAuthHandler.AddTo);

        Assert.Equal(HttpStatusCode.Unauthorized, await SendAsync(host, HttpMethod.Get, "/hermex/api/settings", null));
        Assert.Equal(HttpStatusCode.Unauthorized, await SendAsync(host, HttpMethod.Delete, "/hermex/api/messages", null));
        Assert.Equal(HttpStatusCode.OK, await SendAsync(host, HttpMethod.Get, "/hermex/api/settings", ViewerRoles));
    }

    [Fact]
    public async Task No_policies_keeps_everything_open()
    {
        await using var host = await HermexTestHost.StartAsync();

        Assert.Equal(HttpStatusCode.OK, await SendAsync(host, HttpMethod.Get, "/hermex/settings", null));
        Assert.Equal(HttpStatusCode.OK,
            await SendAsync(host, HttpMethod.Delete, "/hermex/api/messages", null, requestHeader: false));
    }

    [Fact]
    public async Task State_changing_calls_need_the_request_header_when_policies_are_set()
    {
        await using var host = await StartWithPoliciesAsync();

        Assert.Equal(HttpStatusCode.BadRequest,
            await SendAsync(host, HttpMethod.Delete, "/hermex/api/messages", AdminRoles, requestHeader: false));
        Assert.Equal(HttpStatusCode.OK,
            await SendAsync(host, HttpMethod.Delete, "/hermex/api/messages", AdminRoles));
        Assert.Equal(HttpStatusCode.OK,
            await SendAsync(host, HttpMethod.Get, "/hermex/api/messages", ViewerRoles, requestHeader: false));
    }

    [Fact]
    public async Task Assets_are_anonymous_unless_the_host_turns_that_off()
    {
        await using (var open = await StartWithPoliciesAsync())
            Assert.Equal(HttpStatusCode.OK, await SendAsync(open, HttpMethod.Get, "/hermex/assets/css/hermex.css", null));

        await using var closed = await StartWithPoliciesAsync(o => o.Authorization.AllowAnonymousAssets = false);
        Assert.Equal(HttpStatusCode.Unauthorized,
            await SendAsync(closed, HttpMethod.Get, "/hermex/assets/css/hermex.css", null));
    }

    [Fact]
    public async Task Server_detail_in_status_is_admin_only()
    {
        await using var host = await StartWithPoliciesAsync();

        async Task<JsonElement> ServerAsync(string roles)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/hermex/api/status");
            request.Headers.Add(TestAuthHandler.RolesHeader, roles);
            using var response = await host.Client.SendAsync(request);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return doc.RootElement.GetProperty("server").Clone();
        }

        Assert.Equal(JsonValueKind.Null, (await ServerAsync(ViewerRoles)).GetProperty("databasePath").ValueKind);
        Assert.Equal(JsonValueKind.String, (await ServerAsync(AdminRoles)).GetProperty("databasePath").ValueKind);
    }

    [Fact]
    public async Task Admin_detail_honours_a_policy_with_its_own_scheme()
    {
        // No default scheme; the Admin policy names its scheme; Viewer is open.
        await using var host = await HermexTestHost.StartAsync(
            o => o.Authorization.AdminPolicy = "AdminOnScheme",
            s =>
            {
                s.AddAuthentication().AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, null);
                s.AddAuthorization(a => a.AddPolicy("AdminOnScheme", p => p
                    .AddAuthenticationSchemes(TestAuthHandler.SchemeName)
                    .RequireRole("admin")));
            });

        using var request = new HttpRequestMessage(HttpMethod.Get, "/hermex/api/status");
        request.Headers.Add(TestAuthHandler.RolesHeader, "admin");
        using var response = await host.Client.SendAsync(request);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(JsonValueKind.String, doc.RootElement.GetProperty("server").GetProperty("databasePath").ValueKind);
    }

    [Fact]
    public async Task Startup_fails_when_a_policy_is_not_registered()
    {
        var ex = await Assert.ThrowsAsync<HermexConfigurationException>(() => HermexTestHost.StartAsync(
            o => o.Authorization.ViewerPolicy = "Missing", TestAuthHandler.AddTo));
        Assert.Contains("Missing", ex.Message);
    }

    [Fact]
    public async Task Startup_fails_when_there_is_no_authentication_scheme()
    {
        await Assert.ThrowsAsync<HermexConfigurationException>(() => HermexTestHost.StartAsync(
            o => o.Authorization.ViewerPolicy = "SignedIn",
            s => s.AddAuthorization(a => a.AddPolicy("SignedIn", p => p.RequireAuthenticatedUser()))));
    }

    [Theory]
    [InlineData(null, true)]        // no Admin policy: everyone gets live logs, as in 2.0.0
    [InlineData("admin", true)]
    [InlineData("manager", false)]
    public async Task Only_admin_connections_join_the_live_logs_group(string? role, bool joins)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorization(o => o.AddPolicy("Admin", p => p.RequireRole("admin")));
        await using var provider = services.BuildServiceProvider();

        var options = new HermexOptions();
        if (role is not null)
            options.Authorization.AdminPolicy = "Admin";

        var identity = new ClaimsIdentity(
            role is null ? Array.Empty<Claim>() : new[] { new Claim(ClaimTypes.Role, role) }, "Test");
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(identity), RequestServices = provider };
        var groups = new RecordingGroupManager();
        var hub = new InboxHub(options)
        {
            Context = new FakeCallerContext(http),
            Groups = groups,
        };

        await hub.OnConnectedAsync();

        Assert.Equal(joins, groups.Joined.Contains("hermex-logs"));
    }

    private sealed class RecordingGroupManager : IGroupManager
    {
        public List<string> Joined { get; } = new();

        public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
        {
            Joined.Add(groupName);
            return Task.CompletedTask;
        }

        public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class FakeCallerContext : HubCallerContext
    {
        public FakeCallerContext(HttpContext http)
        {
            User = http.User;
            Features.Set<IHttpContextFeature>(new HttpContextFeature { HttpContext = http });
        }

        private sealed class HttpContextFeature : IHttpContextFeature
        {
            public HttpContext? HttpContext { get; set; }
        }

        public override string ConnectionId => "test-connection";
        public override string? UserIdentifier => null;
        public override ClaimsPrincipal? User { get; }
        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
        public override IFeatureCollection Features { get; } = new FeatureCollection();
        public override CancellationToken ConnectionAborted => CancellationToken.None;
        public override void Abort() { }
    }
}
