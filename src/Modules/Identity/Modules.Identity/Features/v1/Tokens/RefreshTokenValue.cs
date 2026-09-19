using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Boilerplate.Modules.Identity.Features.v1.Tokens;

/// <summary>
/// The opaque refresh token of ADR-0002: <c>"{tenantId}.{32 CSPRNG bytes, base64url}"</c>.
///
/// The prefix is routing metadata, not a credential — it tells the anonymous refresh call which
/// tenant to resolve, and the secret is then matched by SHA-256 hash *inside* that tenant's query
/// filter. A token waved under another tenant's prefix therefore matches no row: the prefix cannot
/// be used to reach across tenants, only to fail faster.
///
/// The token is never stored. Only <see cref="Hash"/> of it is, so a database dump yields nothing
/// replayable. base64url keeps the value safe in a cookie, a URL and a JSON body without escaping.
/// </summary>
internal static class RefreshTokenValue
{
    /// <summary>Entropy of the secret half, in bytes.</summary>
    public const int SecretByteLength = 32;

    private const char Separator = '.';

    public static string Issue(string tenantId)
    {
        var secret = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(SecretByteLength));
        return string.Create(CultureInfo.InvariantCulture, $"{tenantId}{Separator}{secret}");
    }

    /// <summary>
    /// Splits the token at its LAST separator: base64url never contains one, so everything before
    /// it is the tenant Id — which may legitimately contain dots.
    /// </summary>
    public static bool TryGetTenantId(string? token, [NotNullWhen(true)] out string? tenantId)
    {
        tenantId = null;
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        var separator = token.LastIndexOf(Separator);
        if (separator <= 0 || separator == token.Length - 1)
        {
            return false;
        }

        tenantId = token[..separator];
        return true;
    }

    /// <summary>The at-rest form of the token. SHA-256 is enough: the input is 32 random bytes, not a password.</summary>
    public static string Hash(string token)
    {
        return Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    }
}
