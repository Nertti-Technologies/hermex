using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Hermex.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Hermex.Tests;

/// <summary>ADR-0002 D1 (settings lock), D2 (relay allow-list) and D5 (public settings API).</summary>
public sealed class HostSettingsTests
{
    private static Func<Microsoft.AspNetCore.Builder.WebApplication, Task> SeedStoredSettings(Action<HermexSettings> change) =>
        async app =>
        {
            var store = app.Services.GetRequiredService<IMailStore>();
            await store.InitializeAsync();
            var settings = new HermexSettings();
            change(settings);
            await store.SaveSettingsAsync(new Dictionary<string, string>
            {
                ["hermex.settings"] = JsonSerializer.Serialize(settings),
            });
        };

    [Fact]
    public async Task Stored_settings_are_applied_by_default()
    {
        await using var host = await HermexTestHost.StartAsync(
            beforeUseHermex: SeedStoredSettings(s => s.DashboardTitle = "Stored title"));

        Assert.Equal("Stored title", host.Options.DashboardTitle);
    }

    [Fact]
    public async Task Stored_settings_are_ignored_when_runtime_settings_are_off()
    {
        await using var host = await HermexTestHost.StartAsync(
            o => o.AllowRuntimeSettings = false,
            beforeUseHermex: SeedStoredSettings(s =>
            {
                s.RelayEnabled = true;
                s.RelayHost = "smtp.elsewhere.test";
            }));

        Assert.False(host.Options.Relay.Enabled);
        Assert.Equal("localhost", host.Options.Relay.Host);

        // The stored row is kept, so turning runtime settings back on restores it.
        var stored = await host.Services.GetRequiredService<IMailStore>().GetSettingsAsync();
        Assert.True(stored.ContainsKey("hermex.settings"));
    }

    [Fact]
    public async Task Updates_are_locked_through_the_api_and_the_service()
    {
        await using var host = await HermexTestHost.StartAsync(o => o.AllowRuntimeSettings = false);
        var settings = host.Services.GetRequiredService<IHermexSettings>();
        var proposed = settings.GetCurrent();
        proposed.RelayEnabled = true;

        var result = await settings.UpdateAsync(proposed);
        Assert.Equal(HermexSettingsUpdateStatus.Locked, result.Status);
        Assert.False(result.Success);

        var response = await host.Client.PutAsJsonAsync("/hermex/api/settings", proposed);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(host.Options.Relay.Enabled);
    }

    [Fact]
    public async Task Locked_settings_page_is_read_only()
    {
        await using var host = await HermexTestHost.StartAsync(o => o.AllowRuntimeSettings = false);

        var html = await host.Client.GetStringAsync("/hermex/settings");

        Assert.Contains("Configured by the host application", html);
        Assert.Contains("disabled", html);
        Assert.DoesNotContain("m4d-settings-save", html);
    }

    [Fact]
    public async Task Status_reports_whether_runtime_settings_are_allowed()
    {
        await using var host = await HermexTestHost.StartAsync(o => o.AllowRuntimeSettings = false);

        using var status = JsonDocument.Parse(await host.Client.GetStringAsync("/hermex/api/status"));

        Assert.False(status.RootElement.GetProperty("options").GetProperty("allowRuntimeSettings").GetBoolean());
    }

    [Fact]
    public async Task Earlier_registration_names_still_work()
    {
        var directory = Path.Combine(Path.GetTempPath(), "hermex-tests", Guid.NewGuid().ToString("N"));
        var builder = WebApplication.CreateBuilder(
            new WebApplicationOptions { ContentRootPath = Path.GetTempPath() });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
#pragma warning disable CS0618 // in case the earlier names are marked obsolete later
        builder.Services.AddMail4Dev(o =>
        {
            o.SmtpPort = HermexTestHost.FreePort();
            o.DatabasePath = Path.Combine(directory, "hermex.db");
            o.DashboardTitle = "Earlier names";
        });
        await using var app = builder.Build();
        app.UseMail4Dev();
#pragma warning restore CS0618
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
            var html = await client.GetStringAsync("/hermex");
            Assert.Contains("Earlier names", html);
        }
        finally
        {
            await app.StopAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(directory, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task Settings_service_is_public_and_saves()
    {
        await using var host = await HermexTestHost.StartAsync();
        var settings = host.Services.GetRequiredService<IHermexSettings>();
        var proposed = settings.GetCurrent();
        proposed.DashboardTitle = "From host code";

        var result = await settings.UpdateAsync(proposed);

        Assert.Equal(HermexSettingsUpdateStatus.Saved, result.Status);
        Assert.Equal("From host code", host.Options.DashboardTitle);
    }

    [Fact]
    public async Task Relay_host_outside_allow_list_is_rejected_by_validation()
    {
        await using var host = await HermexTestHost.StartAsync(o => o.Relay.AllowedHosts.Add("smtp.allowed.test"));
        var settings = host.Services.GetRequiredService<IHermexSettings>();
        var proposed = settings.GetCurrent();
        proposed.RelayEnabled = true;
        proposed.RelayHost = "169.254.169.254";

        var result = await settings.UpdateAsync(proposed);
        Assert.Equal(HermexSettingsUpdateStatus.Invalid, result.Status);

        proposed.RelayHost = "SMTP.Allowed.Test";
        Assert.Equal(HermexSettingsUpdateStatus.Saved, (await settings.UpdateAsync(proposed)).Status);
    }

    [Fact]
    public void Relay_host_outside_allow_list_is_rejected_in_code_options()
    {
        var options = new HermexOptions();
        options.Relay.Enabled = true;
        options.Relay.Host = "smtp.other.test";
        options.Relay.AllowedHosts.Add("smtp.allowed.test");

        Assert.Throws<HermexConfigurationException>(options.Validate);
    }

    [Fact]
    public async Task Relay_service_rechecks_allow_list_before_connecting()
    {
        await using var host = await HermexTestHost.StartAsync(o =>
        {
            o.Relay.Enabled = true;
            o.Relay.Host = "smtp.allowed.test";
            o.Relay.AllowedHosts.Add("smtp.allowed.test");
        });
        var id = await host.SeedMessageAsync();

        // Options mutated in code after startup bypass settings validation.
        host.Options.Relay.Host = "127.0.0.1";
        var result = await host.Services.GetRequiredService<Hermex.Smtp.IMailRelayService>().RelayAsync(id);

        Assert.False(result.Success);
        Assert.Contains("not in the host application's list", result.Error);
    }
}
