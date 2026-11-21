using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Hermex.Internal;

/// <summary>
/// Standard Webhooks signing (https://www.standardwebhooks.com/, spec Apache-2.0; ADR-0003 D4):
/// <c>webhook-signature: v1,&lt;base64(HMAC-SHA256(secret, "{id}.{timestamp}.{body}"))&gt;</c>, one entry per secret,
/// separated by spaces.
/// </summary>
internal static class WebhookSigner
{
    private const string SecretPrefix = "whsec_";

    /// <summary>Secret length range in bytes (Standard Webhooks recommends 24–64).</summary>
    private const int MinSecretBytes = 24, MaxSecretBytes = 64;

    public static bool TryDecodeSecret(string? secret, out byte[] key)
    {
        key = Array.Empty<byte>();
        if (secret is null || !secret.StartsWith(SecretPrefix, StringComparison.Ordinal))
            return false;
        var encoded = secret[SecretPrefix.Length..];
        // Base64 of 64 bytes is 88 characters; refuse anything longer before allocating.
        if (encoded.Length > 100)
            return false;
        var buffer = new byte[encoded.Length];
        if (!Convert.TryFromBase64String(encoded, buffer, out var written) || written is < MinSecretBytes or > MaxSecretBytes)
            return false;
        key = buffer[..written];
        return true;
    }

    /// <summary>Returns the <c>webhook-signature</c> header value for the given secrets.</summary>
    public static string Sign(string id, long timestamp, ReadOnlySpan<byte> body, IEnumerable<byte[]> keys)
    {
        var prefix = Encoding.UTF8.GetBytes(id + "." + timestamp.ToString(CultureInfo.InvariantCulture) + ".");
        var signed = new byte[prefix.Length + body.Length];
        prefix.CopyTo(signed, 0);
        body.CopyTo(signed.AsSpan(prefix.Length));
        return string.Join(' ', keys.Select(key => "v1," + Convert.ToBase64String(HMACSHA256.HashData(key, signed))));
    }
}
