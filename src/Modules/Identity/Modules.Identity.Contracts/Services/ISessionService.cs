using Boilerplate.Modules.Identity.Contracts.DTOs;

namespace Boilerplate.Modules.Identity.Contracts.Services;

/// <summary>
/// The one session store (ADR-0002): a tenant-isolated row per signed-in device that *is* the
/// refresh token. Minting and rotation live here rather than in the token service because both
/// are database operations that must stay inside the tenant's query filter.
/// </summary>
public interface ISessionService
{
    /// <summary>
    /// Opens a session for a device and mints its first refresh token. Throws when the session
    /// cannot be written — a login that leaves no session row has no way to refresh and no way to
    /// be revoked, so it must fail rather than succeed silently.
    /// </summary>
    Task<SessionTokenDto> CreateSessionAsync(
        string userId,
        string ipAddress,
        string userAgent,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Spends a refresh token and mints its successor in a single compare-and-set, so N concurrent
    /// callers produce exactly one new token. Presenting a token that was already rotated away
    /// revokes the session; see <see cref="SessionRotationStatus"/> for the failure modes.
    /// </summary>
    Task<SessionRotationDto> RotateRefreshTokenAsync(
        string refreshToken,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Ends the session a refresh token belongs to — the logout path for a caller whose access
    /// token has already expired or was never stored. Matches the previous hash too, so a client
    /// logging out mid-rotation can still close its session. Returns false when the token matches
    /// nothing in this tenant; callers must not turn that into a distinguishable response.
    /// </summary>
    Task<bool> RevokeSessionByRefreshTokenAsync(
        string refreshToken,
        CancellationToken cancellationToken = default);

    Task<List<UserSessionDto>> GetUserSessionsAsync(
        string userId,
        CancellationToken cancellationToken = default);

    Task<List<UserSessionDto>> GetUserSessionsForAdminAsync(
        string userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns all sessions across the current tenant for admin views.
    /// </summary>
    /// <param name="includeInactive">When true, also returns expired/revoked sessions.</param>
    /// <param name="search">Optional substring filter applied to user name, email, or IP address.</param>
    /// <param name="skip">Pagination offset.</param>
    /// <param name="take">Pagination size (capped server-side).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<(List<UserSessionDto> Items, long TotalCount)> GetTenantSessionsAsync(
        bool includeInactive,
        string? search,
        int skip,
        int take,
        CancellationToken cancellationToken = default);

    Task<UserSessionDto?> GetSessionAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default);

    Task<bool> RevokeSessionAsync(
        Guid sessionId,
        string revokedBy,
        string? reason = null,
        CancellationToken cancellationToken = default);

    Task<int> RevokeAllSessionsAsync(
        string userId,
        string revokedBy,
        Guid? exceptSessionId = null,
        string? reason = null,
        CancellationToken cancellationToken = default);

    Task<int> RevokeAllSessionsForAdminAsync(
        string userId,
        string revokedBy,
        string? reason = null,
        CancellationToken cancellationToken = default);

    Task<bool> RevokeSessionForAdminAsync(
        Guid sessionId,
        string revokedBy,
        string? reason = null,
        CancellationToken cancellationToken = default);

    Task CleanupExpiredSessionsAsync(
        CancellationToken cancellationToken = default);
}