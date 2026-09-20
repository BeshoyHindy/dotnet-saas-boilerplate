using System.Security.Claims;

namespace Boilerplate.Modules.Identity.Contracts.Services;

/// <summary>
/// Mints access tokens only. Refresh tokens belong to <see cref="ISessionService"/>: they are rows
/// in the tenant-isolated session store, not signed artefacts, and minting one without a session
/// would produce a credential nothing can revoke (ADR-0002).
/// </summary>
public interface ITokenService
{
    /// <summary>
    /// Issues an access token. Pass <paramref name="lifetime"/> to override the default
    /// <c>JwtOptions.AccessTokenMinutes</c> (impersonation uses this to let the operator
    /// pick 10/15/30 min sessions).
    /// </summary>
    Task<(string AccessToken, DateTime ExpiresAtUtc)> IssueAccessOnlyAsync(
        string subject,
        IEnumerable<Claim> claims,
        TimeSpan? lifetime = null,
        CancellationToken ct = default);
}