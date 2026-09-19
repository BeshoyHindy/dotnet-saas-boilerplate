namespace Boilerplate.Modules.Identity.Contracts.DTOs;

/// <summary>
/// A freshly-minted refresh token and the session row that backs it. The raw token exists only
/// here and in the response that carries it to the caller — the store keeps a SHA-256 hash.
/// </summary>
public sealed record SessionTokenDto(
    Guid SessionId,
    string RefreshToken,
    DateTime RefreshTokenExpiresAt);

/// <summary>Why a refresh-token rotation did not produce a new token.</summary>
public enum SessionRotationStatus
{
    /// <summary>The compare-and-set won: the caller holds the only live token for this session.</summary>
    Rotated,

    /// <summary>No session in this tenant holds that hash — wrong tenant, unknown or long-expired token.</summary>
    NotFound,

    /// <summary>The token had already been rotated away. The session is revoked as a consequence.</summary>
    Reused,

    /// <summary>The session was revoked (logout, admin action, an earlier reuse).</summary>
    Revoked,

    /// <summary>The session outlived its refresh window.</summary>
    Expired,

    /// <summary>The user's security stamp moved (password change, credential reset) after the session began.</summary>
    SecurityStampChanged,

    /// <summary>A concurrent caller rotated first; this token is no longer current.</summary>
    Superseded,
}

/// <summary>
/// The outcome of <c>ISessionService.RotateRefreshTokenAsync</c>. Everything but
/// <see cref="SessionRotationStatus.Rotated"/> leaves <see cref="RefreshToken"/> null.
/// </summary>
public sealed record SessionRotationDto(
    SessionRotationStatus Status,
    Guid SessionId,
    string? UserId,
    string? RefreshToken,
    DateTime RefreshTokenExpiresAt)
{
    public static SessionRotationDto Failed(SessionRotationStatus status, Guid sessionId = default, string? userId = null) =>
        new(status, sessionId, userId, RefreshToken: null, RefreshTokenExpiresAt: default);
}
