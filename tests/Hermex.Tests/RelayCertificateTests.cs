using Hermex.Smtp;
using Hermex.Storage;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Hermex.Tests;

/// <summary>
/// HermexRelayOptions.ValidateCertificate, relaying to a second Hermex host that acts as the
/// upstream server (STARTTLS with its generated self-signed certificate, or no TLS at all).
/// </summary>
public sealed class RelayCertificateTests
{
    private static async Task<(MailRelayResult Result, long UpstreamCount, bool UpstreamSawTls)> RelayAsync(
        bool upstreamTls, bool validateCertificate, bool useStartTls = true)
    {
        await using var upstream = await HermexTestHost.StartAsync(o =>
            o.TlsMode = upstreamTls ? HermexTlsMode.StartTls : HermexTlsMode.None);
        var upstreamPort = await upstream.WaitForSmtpPortAsync();

        await using var host = await HermexTestHost.StartAsync(o =>
        {
            o.Relay.Enabled = true;
            o.Relay.Host = "127.0.0.1";
            o.Relay.Port = upstreamPort;
            o.Relay.UseStartTls = useStartTls;
            o.Relay.ValidateCertificate = validateCertificate;
        });
        var id = await host.SeedMessageAsync();

        var result = await host.Services.GetRequiredService<IMailRelayService>().RelayAsync(id);

        // Give the upstream's persistence loop time to store what it received (flushes every 20 ms).
        var store = upstream.Services.GetRequiredService<IMailStore>();
        var deadline = DateTime.UtcNow.AddSeconds(result.Success ? 5 : 0.3);
        long count;
        do
        {
            count = (await store.GetStatsAsync()).TotalMessages;
            if (count > 0)
                break;
            await Task.Delay(25);
        } while (DateTime.UtcNow < deadline);

        var sawTls = false;
        if (count > 0)
        {
            var first = (await store.GetMessagesAsync(new MessageQuery())).Items[0];
            sawTls = (await store.GetMessageAsync(first.Id))!.SecuredWithTls;
        }
        return (result, count, sawTls);
    }

    [Fact]
    public async Task Self_signed_upstream_is_accepted_by_default()
    {
        var (result, upstreamCount, sawTls) = await RelayAsync(upstreamTls: true, validateCertificate: false);

        Assert.True(result.Success, result.Error);
        Assert.Equal(1, upstreamCount);
        Assert.True(sawTls); // really went over TLS, not a plain-text fallback
    }

    [Fact]
    public async Task Self_signed_upstream_is_refused_when_validating()
    {
        var (result, upstreamCount, _) = await RelayAsync(upstreamTls: true, validateCertificate: true);

        Assert.False(result.Success);
        Assert.DoesNotContain("STARTTLS", result.Error); // refused for the certificate, not a missing STARTTLS
        Assert.Contains("certificate", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, upstreamCount);
    }

    [Fact]
    public async Task Missing_starttls_is_refused_when_validating()
    {
        var (result, upstreamCount, _) = await RelayAsync(upstreamTls: false, validateCertificate: true);

        Assert.False(result.Success);
        Assert.Contains("STARTTLS", result.Error);
        Assert.Equal(0, upstreamCount);
    }

    [Fact]
    public async Task Missing_starttls_still_relays_in_plain_text_by_default()
    {
        var (result, upstreamCount, sawTls) = await RelayAsync(upstreamTls: false, validateCertificate: false);

        Assert.True(result.Success, result.Error);
        Assert.Equal(1, upstreamCount);
        Assert.False(sawTls);
    }

    [Fact]
    public async Task Validation_implies_starttls_even_when_use_starttls_is_off()
    {
        // The README's team example sets ValidateCertificate without UseStartTls.
        var (plain, plainCount, _) = await RelayAsync(upstreamTls: false, validateCertificate: true, useStartTls: false);
        Assert.False(plain.Success);
        Assert.Contains("STARTTLS", plain.Error);
        Assert.Equal(0, plainCount);

        // Against a TLS upstream it upgrades and then fails on the self-signed certificate.
        var (tls, tlsCount, _) = await RelayAsync(upstreamTls: true, validateCertificate: true, useStartTls: false);
        Assert.False(tls.Success);
        Assert.DoesNotContain("STARTTLS", tls.Error);
        Assert.DoesNotContain("ValidateCertificate", tls.Error);
        Assert.Contains("certificate", tls.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, tlsCount);
    }
}
