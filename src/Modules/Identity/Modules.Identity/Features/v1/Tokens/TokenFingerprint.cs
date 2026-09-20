using System.Security.Cryptography;
using System.Text;

namespace Boilerplate.Modules.Identity.Features.v1.Tokens;

/// <summary>
/// Short, printable fingerprint of a token. Access and refresh tokens are secrets, so audit rows and
/// session records store only this — never the token itself. Truncating SHA-256 to 8 bytes keeps the
/// value readable in a log line while staying far too wide to collide across a tenant's sessions.
/// </summary>
internal static class TokenFingerprint
{
    public static string Sha256Short(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash.AsSpan(0, 8));
    }
}
