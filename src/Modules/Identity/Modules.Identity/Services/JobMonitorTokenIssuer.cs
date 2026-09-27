using Boilerplate.Modules.Identity.Authorization.Jwt;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;

namespace Boilerplate.Modules.Identity.Services;

/// <summary>
/// Mints the token carried by the Job monitor cookie (ADR-0009): the caller's own identity claims —
/// subject, tenant and <c>sid</c> among them — re-signed for the Job monitor audience with a fixed
/// <see cref="JobMonitorCookie.Lifetime"/>. The audience is what keeps it from working as a bearer
/// token anywhere, and the <c>sid</c> is what lets revoking the session end it.
/// </summary>
public sealed class JobMonitorTokenIssuer(IOptions<JwtOptions> options, TimeProvider timeProvider)
{
    // Claims about the token rather than the person; each is re-minted for the new token.
    private static readonly HashSet<string> TokenClaims = new(StringComparer.Ordinal)
    {
        JwtRegisteredClaimNames.Iss,
        JwtRegisteredClaimNames.Aud,
        JwtRegisteredClaimNames.Exp,
        JwtRegisteredClaimNames.Nbf,
        JwtRegisteredClaimNames.Iat,
        JwtRegisteredClaimNames.Jti,
    };

    private readonly JwtOptions _options = options.Value;

    public (string Token, DateTime ExpiresAtUtc) Issue(IEnumerable<Claim> callerClaims)
    {
        ArgumentNullException.ThrowIfNull(callerClaims);

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var expiresAt = now.Add(JobMonitorCookie.Lifetime);

        var claims = callerClaims
            .Where(c => !TokenClaims.Contains(c.Type))
            .DistinctBy(c => (c.Type, c.Value))
            .Select(c => new Claim(c.Type, c.Value, c.ValueType))
            .Append(new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")))
            .Append(new Claim(
                JwtRegisteredClaimNames.Iat,
                EpochTime.GetIntDate(now).ToString(CultureInfo.InvariantCulture),
                ClaimValueTypes.Integer64));

        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.SigningKey)),
            SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            _options.Issuer,
            JobMonitorCookieAuthentication.AudienceFor(_options),
            claims,
            notBefore: now,
            expires: expiresAt,
            signingCredentials: credentials);

        return (new JwtSecurityTokenHandler().WriteToken(token), expiresAt);
    }
}
