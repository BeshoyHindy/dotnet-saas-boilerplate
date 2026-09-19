using System.Security.Claims;

namespace Boilerplate.Modules.Identity.Contracts.Services;

public interface IIdentityService
{
    /// <summary>
    /// Validates the provided user credentials and returns a unique subject ID with associated claims.
    /// </summary>
    /// <param name="email">User email or username</param>
    /// <param name="password">User password</param>
    /// <param name="twoFactorCode">Optional two-factor authentication code</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Subject ID and claims, or null if invalid</returns>
    Task<(string Subject, IEnumerable<Claim> Claims)?>
        ValidateCredentialsAsync(string email, string password, string? twoFactorCode = null, CancellationToken ct = default);

    /// <summary>
    /// Rebuilds the claim set for a user of the *current* tenant after their session's refresh token
    /// has been rotated, re-running the account and tenant status checks that login runs. The lookup
    /// stays inside the tenant query filter, so the refresh path can never reach across tenants.
    /// Returns null if the user is not in this tenant.
    /// </summary>
    Task<(string Subject, IEnumerable<Claim> Claims)?>
        BuildClaimsForRefreshAsync(string userId, CancellationToken ct = default);

    /// <summary>
    /// Builds the claim set for a user located in an arbitrary tenant, bypassing Finbuckle's tenant
    /// query filters. Used for impersonation and end-impersonation flows where the current request's
    /// tenant context differs from the target user's tenant. Returns null if the user is not found.
    /// </summary>
    Task<(string Subject, IEnumerable<Claim> Claims)?>
        BuildClaimsForUserAsync(string userId, string tenantId, CancellationToken ct = default);
}