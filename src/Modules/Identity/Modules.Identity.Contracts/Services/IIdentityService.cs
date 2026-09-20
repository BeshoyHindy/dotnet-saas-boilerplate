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

    /// <summary>
    /// Locates a user inside an arbitrary tenant by id or by email, bypassing Finbuckle's tenant
    /// query filters. The operator token exchange uses it to resolve the subject it is about to
    /// act as (explicit <paramref name="userId"/>, else the tenant record's admin email) and to
    /// turn "missing" and "deactivated" into distinct, deliberate status codes instead of the
    /// generic 401 the claim builder throws. Returns null when nothing matches.
    /// </summary>
    Task<TenantUserLookup?> FindTenantUserAsync(
        string tenantId,
        string? userId = null,
        string? email = null,
        CancellationToken ct = default);
}

/// <summary>Minimal projection of a user in some tenant — enough to decide whether we may act as them.</summary>
public sealed record TenantUserLookup(
    string UserId,
    string? UserName,
    string? Email,
    bool IsActive,
    bool EmailConfirmed);