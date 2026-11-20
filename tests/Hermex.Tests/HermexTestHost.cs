using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Hermex.Storage;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hermex.Tests;

/// <summary>
/// A real Hermex web host on Kestrel, bound to a free loopback port, over a throwaway database.
/// </summary>
internal sealed class HermexTestHost : IAsyncDisposable
{
    private readonly string _directory;

    private HermexTestHost(WebApplication app, string directory, HttpClient client)
    {
        App = app;
        _directory = directory;
        Client = client;
    }

    public WebApplication App { get; }
    public HttpClient Client { get; }
    public IServiceProvider Services => App.Services;
    public HermexOptions Options => App.Services.GetRequiredService<HermexOptions>();

    /// <param name="configure">Hermex options.</param>
    /// <param name="services">Extra host services (authentication, policies, hooks).</param>
    /// <param name="beforeUseHermex">Runs after Build, before UseHermex (e.g. to seed stored settings).</param>
    /// <param name="directory">Reuse a database directory from an earlier host.</param>
    public static async Task<HermexTestHost> StartAsync(
        Action<HermexOptions>? configure = null,
        Action<IServiceCollection>? services = null,
        Func<WebApplication, Task>? beforeUseHermex = null,
        string? directory = null)
    {
        directory ??= Path.Combine(Path.GetTempPath(), "hermex-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Development",
            ContentRootPath = directory,
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        builder.Services.AddHermex(o =>
        {
            o.SmtpPort = FreePort();
            o.DatabasePath = Path.Combine(directory, "hermex.db");
            o.PersistenceFlushInterval = TimeSpan.FromMilliseconds(20);
            configure?.Invoke(o);
        });
        services?.Invoke(builder.Services);

        var app = builder.Build();
        if (beforeUseHermex is not null)
            await beforeUseHermex(app);
        app.UseHermex();
        await app.StartAsync();

        var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        return new HermexTestHost(app, directory, client);
    }

    /// <summary>Stores a message directly, bypassing SMTP, and returns its id.</summary>
    public async Task<long> SeedMessageAsync()
    {
        var raw = TestSupport.Raw("From: sender@example.test\nTo: recipient@example.test\nSubject: Seeded\n\nBody\n");
        var record = new MessageRecord
        {
            SessionId = "test",
            ReceivedAtUtc = DateTimeOffset.UtcNow,
            EnvelopeFrom = "sender@example.test",
            EnvelopeTo = "recipient@example.test",
            FromAddress = "sender@example.test",
            FromDisplay = "Sender",
            ToDisplay = "recipient@example.test",
            Subject = "Seeded",
            HasText = true,
            Recipients = new[] { "recipient@example.test" },
            RawData = raw,
            RawSize = raw.Length,
        };
        await Services.GetRequiredService<IMailStore>().AddMessagesAsync(new[] { record });
        return record.Id;
    }

    /// <summary>Keeps the directory so a second host can reuse the database.</summary>
    public async Task StopAsync()
    {
        Client.Dispose();
        await App.StopAsync();
        await App.DisposeAsync();
        SqliteConnection.ClearAllPools();
    }

    public string DatabaseDirectory => _directory;

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        try { Directory.Delete(_directory, recursive: true); }
        catch { /* best effort */ }
    }

    public static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}

/// <summary>
/// Test sign-in: the <c>X-Test-Roles</c> header signs the request in with those roles
/// (comma-separated, may be empty); no header means anonymous.
/// </summary>
internal sealed class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "Test";
    public const string RolesHeader = "X-Test-Roles";

    public TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : base(options, logger, encoder) { }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(RolesHeader, out var roles))
            return Task.FromResult(AuthenticateResult.NoResult());

        var claims = new List<Claim> { new(ClaimTypes.Name, "tester") };
        foreach (var role in roles.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            claims.Add(new Claim(ClaimTypes.Role, role));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
    }

    /// <summary>Registers the scheme and the policies SignedIn, Manage and Admin.</summary>
    public static void AddTo(IServiceCollection services)
    {
        services.AddAuthentication(SchemeName).AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(SchemeName, null);
        services.AddAuthorization(o =>
        {
            o.AddPolicy("SignedIn", p => p.RequireAuthenticatedUser());
            o.AddPolicy("Manage", p => p.RequireRole("manager"));
            o.AddPolicy("Admin", p => p.RequireRole("admin"));
        });
    }
}
