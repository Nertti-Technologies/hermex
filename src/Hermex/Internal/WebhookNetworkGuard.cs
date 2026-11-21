using System.Net;
using System.Net.Sockets;

namespace Hermex.Internal;

/// <summary>
/// Decides which addresses webhook deliveries may connect to (ADR-0003 D4; OWASP SSRF Prevention Cheat Sheet).
/// The check runs inside <see cref="SocketsHttpHandler.ConnectCallback"/> on the resolved address that is then
/// connected to, so a name that resolves differently later (DNS rebinding) cannot slip past it.
/// </summary>
internal static class WebhookNetworkGuard
{
    /// <summary>What <see cref="HermexWebhookNetworkPolicy.Auto"/> means in this environment.</summary>
    public static bool AllowsPrivate(HermexWebhookNetworkPolicy policy, bool isDevelopment) => policy switch
    {
        HermexWebhookNetworkPolicy.AllowPrivate => true,
        HermexWebhookNetworkPolicy.PublicOnly => false,
        _ => isDevelopment, // Auto fails closed outside Development
    };

    /// <summary>Whether a connection to <paramref name="address"/> is allowed.</summary>
    public static bool IsAllowed(IPAddress address, bool allowPrivate)
    {
        var ip = Normalize(address);
        if (IsAlwaysBlocked(ip))
            return false;
        return allowPrivate || !IsPrivate(ip);
    }

    /// <summary>Unwraps IPv4 carried inside IPv6: mapped ::ffff:a.b.c.d, translated ::ffff:0:a.b.c.d (RFC 2765),
    /// compatible ::a.b.c.d (RFC 4291, deprecated), NAT64 64:ff9b::/96 and 6to4 2002::/16.</summary>
    internal static IPAddress Normalize(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetworkV6)
            return address;
        if (address.IsIPv4MappedToIPv6)
            return address.MapToIPv4();
        var b = address.GetAddressBytes();
        if (address.Equals(IPAddress.IPv6Loopback) || address.Equals(IPAddress.IPv6Any))
            return address;
        if (b.AsSpan(0, 12).IndexOfAnyExcept((byte)0) < 0)                                      // ::a.b.c.d
            return new IPAddress(b.AsSpan(12, 4));
        if (b.AsSpan(0, 8).IndexOfAnyExcept((byte)0) < 0 && b[8] == 0xff && b[9] == 0xff && b[10] == 0 && b[11] == 0)
            return new IPAddress(b.AsSpan(12, 4));                                              // ::ffff:0:a.b.c.d
        if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xff && b[3] == 0x9b && b.AsSpan(4, 8).IndexOfAnyExcept((byte)0) < 0)
            return new IPAddress(b.AsSpan(12, 4));
        if (b[0] == 0x20 && b[1] == 0x02)
            return new IPAddress(b.AsSpan(2, 4));
        return address;
    }

    /// <summary>Cloud instance-metadata and host-agent addresses outside 169.254.0.0/16, blocked under every policy:
    /// Alibaba Cloud metadata, Azure WireServer, AWS IMDS over IPv6.</summary>
    private static readonly IPAddress[] MetadataAddresses =
        { IPAddress.Parse("100.100.100.200"), IPAddress.Parse("168.63.129.16"), IPAddress.Parse("fd00:ec2::254") };

    private static bool IsAlwaysBlocked(IPAddress ip)
    {
        if (MetadataAddresses.Contains(ip))
            return true;
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return b[0] == 0                                   // 0.0.0.0/8 "this network" (RFC 1122)
                || (b[0] == 169 && b[1] == 254)                // 169.254.0.0/16 link-local, cloud metadata (RFC 3927)
                || b[0] >= 224;                                // 224.0.0.0/4 multicast, 240.0.0.0/4 reserved, broadcast
        }
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var b = ip.GetAddressBytes();
            return ip.Equals(IPAddress.IPv6None) || ip.Equals(IPAddress.IPv6Any)
                || ip.IsIPv6LinkLocal                          // fe80::/10
                || ip.IsIPv6Multicast                          // ff00::/8
                || (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0 && b[3] == 0) // 2001::/32 Teredo hides its IPv4
                // 64:ff9b:1::/48 local-use NAT64 (RFC 8215) can carry any IPv4 address, metadata ones included,
                // in an operator-chosen layout that cannot be unwrapped reliably, so it is always refused.
                || (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xff && b[3] == 0x9b && b[4] == 0x00 && b[5] == 0x01);
        }
        return true; // other families are never used for HTTP
    }

    private static bool IsPrivate(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip))
            return true;
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return b[0] == 10                                   // 10.0.0.0/8 (RFC 1918)
                || (b[0] == 172 && (b[1] & 0xF0) == 16)        // 172.16.0.0/12
                || (b[0] == 192 && b[1] == 168)                // 192.168.0.0/16
                || (b[0] == 100 && (b[1] & 0xC0) == 64)        // 100.64.0.0/10 shared address space (RFC 6598)
                || (b[0] == 198 && (b[1] & 0xFE) == 18)        // 198.18.0.0/15 benchmarking (RFC 2544)
                || (b[0] == 192 && b[1] == 0 && b[2] == 0);   // 192.0.0.0/24 protocol assignments (RFC 6890)
        }
        var v6 = ip.GetAddressBytes();
        return (v6[0] & 0xFE) == 0xFC                          // fc00::/7 unique local (RFC 4193)
            || ip.IsIPv6SiteLocal;                             // fec0::/10 deprecated site-local
    }

    /// <summary>
    /// Resolves the host of <paramref name="endPoint"/>, keeps the allowed addresses and connects to the first that answers.
    /// Throws <see cref="WebhookBlockedException"/> when every address is refused.
    /// </summary>
    public static async ValueTask<Stream> ConnectAsync(DnsEndPoint endPoint, bool allowPrivate, CancellationToken cancellationToken)
    {
        var addresses = IPAddress.TryParse(endPoint.Host, out var literal)
            ? new[] { literal }
            : await Dns.GetHostAddressesAsync(endPoint.Host, cancellationToken).ConfigureAwait(false);

        var allowed = addresses.Where(a => IsAllowed(a, allowPrivate)).ToList();
        if (allowed.Count == 0)
            throw new WebhookBlockedException();

        Exception? last = null;
        foreach (var address in allowed)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, endPoint.Port), cancellationToken).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException ex)
            {
                socket.Dispose();
                last = ex;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
        throw last!;
    }
}

/// <summary>The webhook host resolved only to addresses the network policy refuses.</summary>
internal sealed class WebhookBlockedException : Exception
{
    public WebhookBlockedException()
        : base("The webhook address is not allowed by HermexWebhookOptions.NetworkPolicy.")
    {
    }

    public WebhookBlockedException(string message)
        : base(message)
    {
    }

    public WebhookBlockedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
