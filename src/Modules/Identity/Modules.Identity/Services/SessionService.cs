using Finbuckle.MultiTenant.Abstractions;
using Boilerplate.BuildingBlocks.Core.Context;
using Boilerplate.BuildingBlocks.Core.Exceptions;
using Boilerplate.BuildingBlocks.Shared.Multitenancy;
using Boilerplate.Modules.Identity.Authorization.Jwt;
using Boilerplate.Modules.Identity.Contracts.DTOs;
using Boilerplate.Modules.Identity.Contracts.Services;
using Boilerplate.Modules.Identity.Data;
using Boilerplate.Modules.Identity.Domain;
using Boilerplate.Modules.Identity.Features.v1.Tokens;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UAParser;

namespace Boilerplate.Modules.Identity.Services;

public sealed class SessionService : ISessionService
{
    private readonly IdentityDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IMultiTenantContextAccessor<AppTenantInfo> _multiTenantContextAccessor;
    private readonly ILogger<SessionService> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly JwtOptions _jwtOptions;
    private readonly Parser _uaParser;

    public SessionService(
        IdentityDbContext db,
        ICurrentUser currentUser,
        IMultiTenantContextAccessor<AppTenantInfo> multiTenantContextAccessor,
        IOptions<JwtOptions> jwtOptions,
        ILogger<SessionService> logger,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(jwtOptions);
        _db = db;
        _currentUser = currentUser;
        _multiTenantContextAccessor = multiTenantContextAccessor;
        _jwtOptions = jwtOptions.Value;
        _logger = logger;
        _timeProvider = timeProvider;
        _uaParser = Parser.GetDefault();
    }

    private string CurrentTenantId()
    {
        var tenantId = _multiTenantContextAccessor?.MultiTenantContext?.TenantInfo?.Id;
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            throw new UnauthorizedException("Invalid tenant");
        }

        return tenantId;
    }

    private void EnsureValidTenant() => CurrentTenantId();

    public async Task<SessionTokenDto> CreateSessionAsync(
        string userId,
        string ipAddress,
        string userAgent,
        CancellationToken cancellationToken = default)
    {
        var tenantId = CurrentTenantId();

        // The stamp is read inside the tenant filter, so a caller can only ever open a session for
        // a user of the tenant that the request resolved to. Projected into a holder rather than
        // straight to the string so "no such user" stays distinguishable from "stamp is null".
        var user = await _db.Users
            .AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new StampHolder(u.SecurityStamp))
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new UnauthorizedException("user not found");

        var securityStamp = user.SecurityStamp ?? string.Empty;

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var refreshToken = RefreshTokenValue.Issue(tenantId);
        var expiresAt = now.AddDays(_jwtOptions.RefreshTokenDays);
        var clientInfo = _uaParser.Parse(userAgent);

        var session = UserSession.Create(
            userId: userId,
            refreshTokenHash: RefreshTokenValue.Hash(refreshToken),
            securityStamp: securityStamp,
            ipAddress: ipAddress,
            userAgent: userAgent,
            createdAt: now,
            expiresAt: expiresAt,
            deviceType: DeviceTypeClassifier.Classify(clientInfo.Device.Family),
            browser: clientInfo.UA.Family,
            browserVersion: clientInfo.UA.Major,
            operatingSystem: clientInfo.OS.Family,
            osVersion: clientInfo.OS.Major);

        _db.UserSessions.Add(session);
        await _db.SaveChangesAsync(cancellationToken);

        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("Created session {SessionId} for user {UserId}", session.Id, userId);
        }

        return new SessionTokenDto(session.Id, refreshToken, expiresAt);
    }

    public async Task<SessionRotationDto> RotateRefreshTokenAsync(
        string refreshToken,
        CancellationToken cancellationToken = default)
    {
        var tenantId = CurrentTenantId();

        // The prefix is routing metadata. It must agree with the tenant the request already
        // resolved to, otherwise the caller is waving one tenant's token at another's endpoint.
        if (!RefreshTokenValue.TryGetTenantId(refreshToken, out var tokenTenantId)
            || !string.Equals(tokenTenantId, tenantId, StringComparison.Ordinal))
        {
            return SessionRotationDto.Failed(SessionRotationStatus.NotFound);
        }

        var hash = RefreshTokenValue.Hash(refreshToken);
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        // One read, tenant-filtered: the hash can only ever match a row of this tenant, so a
        // foreign token finds nothing no matter which prefix it wears.
        var candidate = await _db.UserSessions
            .AsNoTracking()
            .Where(s => s.RefreshTokenHash == hash || s.PreviousTokenHash == hash)
            .Select(s => new SessionCandidate(
                s.Id,
                s.UserId,
                s.SecurityStamp,
                s.IsRevoked,
                s.ExpiresAt,
                s.RefreshTokenHash == hash))
            .FirstOrDefaultAsync(cancellationToken);

        if (candidate is null)
        {
            return SessionRotationDto.Failed(SessionRotationStatus.NotFound);
        }

        if (!candidate.IsCurrent)
        {
            // Reuse detection (RFC 9700 §4.14.2): the token was already spent, so either it leaked
            // or the legitimate client is confused. Either way the chain is no longer trustworthy —
            // burn the session so the thief and the victim both have to re-authenticate.
            await RevokeForReuseAsync(candidate, now, cancellationToken);
            return SessionRotationDto.Failed(SessionRotationStatus.Reused, candidate.Id, candidate.UserId);
        }

        if (candidate.IsRevoked)
        {
            return SessionRotationDto.Failed(SessionRotationStatus.Revoked, candidate.Id, candidate.UserId);
        }

        if (candidate.ExpiresAt <= now)
        {
            return SessionRotationDto.Failed(SessionRotationStatus.Expired, candidate.Id, candidate.UserId);
        }

        var user = await _db.Users
            .AsNoTracking()
            .Where(u => u.Id == candidate.UserId)
            .Select(u => new StampHolder(u.SecurityStamp))
            .FirstOrDefaultAsync(cancellationToken);

        // A vanished user counts as a stamp change: the session outlived the account it belonged to.
        if (user is null || !string.Equals(user.SecurityStamp ?? string.Empty, candidate.SecurityStamp, StringComparison.Ordinal))
        {
            // A password change, credential reset or 2FA change rotated the stamp. Every session
            // minted against the old one dies with it.
            await RevokeAsync(
                candidate.Id, now, revokedBy: "system", reason: "Security stamp changed", cancellationToken);
            return SessionRotationDto.Failed(
                SessionRotationStatus.SecurityStampChanged, candidate.Id, candidate.UserId);
        }

        var newRefreshToken = RefreshTokenValue.Issue(tenantId);
        var newExpiresAt = now.AddDays(_jwtOptions.RefreshTokenDays);
        var newHash = RefreshTokenValue.Hash(newRefreshToken);

        // The rotation: one compare-and-set. `RefreshTokenHash == hash` is the compare, and the
        // database serialises it, so N concurrent callers holding the same token produce exactly
        // one winner — the losers update zero rows and get nothing.
        var rotated = await _db.UserSessions
            .Where(s => s.Id == candidate.Id && s.RefreshTokenHash == hash && !s.IsRevoked)
            .ExecuteUpdateAsync(
                s => s.SetProperty(x => x.PreviousTokenHash, hash)
                      .SetProperty(x => x.RefreshTokenHash, newHash)
                      .SetProperty(x => x.ExpiresAt, newExpiresAt)
                      .SetProperty(x => x.LastActivityAt, now),
                cancellationToken);

        if (rotated == 0)
        {
            return SessionRotationDto.Failed(SessionRotationStatus.Superseded, candidate.Id, candidate.UserId);
        }

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("Rotated refresh token for session {SessionId}", candidate.Id);
        }

        return new SessionRotationDto(
            SessionRotationStatus.Rotated,
            candidate.Id,
            candidate.UserId,
            newRefreshToken,
            newExpiresAt);
    }

    private async Task RevokeForReuseAsync(SessionCandidate candidate, DateTime now, CancellationToken cancellationToken)
    {
        _logger.LogWarning(
            "Refresh token reuse detected for session {SessionId} (user {UserId}); revoking the session",
            candidate.Id, candidate.UserId);

        await RevokeAsync(candidate.Id, now, revokedBy: "system", reason: "Refresh token reuse detected", cancellationToken);
    }

    private async Task RevokeAsync(
        Guid sessionId, DateTime now, string revokedBy, string reason, CancellationToken cancellationToken)
    {
        // Tracked + SaveChanges (not ExecuteUpdate) so the SessionRevokedEvent is raised and audited.
        var session = await _db.UserSessions
            .FirstOrDefaultAsync(s => s.Id == sessionId && !s.IsRevoked, cancellationToken);

        if (session is null)
        {
            return;
        }

        session.Revoke(now, revokedBy, reason, _multiTenantContextAccessor?.MultiTenantContext?.TenantInfo?.Id);
        await _db.SaveChangesAsync(cancellationToken);
    }

    private sealed record StampHolder(string? SecurityStamp);

    private sealed record SessionCandidate(
        Guid Id,
        string UserId,
        string SecurityStamp,
        bool IsRevoked,
        DateTime ExpiresAt,
        bool IsCurrent);

    public async Task<List<UserSessionDto>> GetUserSessionsAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        EnsureValidTenant();

        var currentUserId = _currentUser.GetUserId().ToString();
        if (!string.Equals(userId, currentUserId, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("Cannot view sessions for another user");
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var sessions = await _db.UserSessions
            .AsNoTracking()
            .Where(s => s.UserId == userId && !s.IsRevoked && s.ExpiresAt > now)
            .OrderByDescending(s => s.LastActivityAt)
            .ToListAsync(cancellationToken);

        return sessions.Select(s => MapToDto(s, isCurrentSession: false)).ToList();
    }

    public async Task<List<UserSessionDto>> GetUserSessionsForAdminAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        EnsureValidTenant();

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var sessions = await _db.UserSessions
            .AsNoTracking()
            .Include(s => s.User)
            .Where(s => s.UserId == userId && !s.IsRevoked && s.ExpiresAt > now)
            .OrderByDescending(s => s.LastActivityAt)
            .ToListAsync(cancellationToken);

        return sessions.Select(s => MapToDto(s, isCurrentSession: false)).ToList();
    }

    public async Task<(List<UserSessionDto> Items, long TotalCount)> GetTenantSessionsAsync(
        bool includeInactive,
        string? search,
        int skip,
        int take,
        CancellationToken cancellationToken = default)
    {
        EnsureValidTenant();

        // Cap server-side so an over-eager client can't pull a tenant's full
        // session table in one round-trip.
        if (take is < 1 or > 200) take = 50;
        if (skip < 0) skip = 0;

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var q = _db.UserSessions
            .AsNoTracking()
            .Include(s => s.User)
            .AsQueryable();

        if (!includeInactive)
        {
            q = q.Where(s => !s.IsRevoked && s.ExpiresAt > now);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            string term = search.Trim();
            q = q.Where(s =>
                (s.User != null && s.User.UserName != null && EF.Functions.ILike(s.User.UserName, $"%{term}%"))
                || (s.User != null && s.User.Email != null && EF.Functions.ILike(s.User.Email, $"%{term}%"))
                || (s.IpAddress != null && EF.Functions.ILike(s.IpAddress, $"%{term}%")));
        }

        long total = await q.LongCountAsync(cancellationToken);

        var sessions = await q
            .OrderByDescending(s => s.LastActivityAt)
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken);

        return (sessions.Select(s => MapToDto(s, isCurrentSession: false)).ToList(), total);
    }

    public async Task<UserSessionDto?> GetSessionAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        EnsureValidTenant();

        var session = await _db.UserSessions
            .AsNoTracking()
            .Include(s => s.User)
            .FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken);

        return session is null ? null : MapToDto(session, isCurrentSession: false);
    }

    public async Task<bool> RevokeSessionAsync(
        Guid sessionId,
        string revokedBy,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        EnsureValidTenant();

        var session = await _db.UserSessions
            .FirstOrDefaultAsync(s => s.Id == sessionId && !s.IsRevoked, cancellationToken);

        if (session is null)
        {
            return false;
        }

        var currentUserId = _currentUser.GetUserId().ToString();
        if (!string.Equals(session.UserId, currentUserId, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("Cannot revoke session for another user");
        }

        var tenantId = _multiTenantContextAccessor?.MultiTenantContext?.TenantInfo?.Id;
        session.Revoke(_timeProvider.GetUtcNow().UtcDateTime, revokedBy, reason ?? "User requested", tenantId);

        await _db.SaveChangesAsync(cancellationToken);

        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("Session {SessionId} revoked by {RevokedBy}", sessionId, revokedBy);
        }

        return true;
    }

    public async Task<int> RevokeAllSessionsAsync(
        string userId,
        string revokedBy,
        Guid? exceptSessionId = null,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        EnsureValidTenant();

        var currentUserId = _currentUser.GetUserId().ToString();
        if (!string.Equals(userId, currentUserId, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("Cannot revoke sessions for another user");
        }

        var query = _db.UserSessions
            .Where(s => s.UserId == userId && !s.IsRevoked);

        if (exceptSessionId.HasValue)
        {
            query = query.Where(s => s.Id != exceptSessionId.Value);
        }

        var sessions = await query.ToListAsync(cancellationToken);

        var tenantId = _multiTenantContextAccessor?.MultiTenantContext?.TenantInfo?.Id;
        var revokedAt = _timeProvider.GetUtcNow().UtcDateTime;
        foreach (var session in sessions)
        {
            session.Revoke(revokedAt, revokedBy, reason ?? "User requested logout from all devices", tenantId);
        }

        await _db.SaveChangesAsync(cancellationToken);

        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("Revoked {Count} sessions for user {UserId}", sessions.Count, userId);
        }

        return sessions.Count;
    }

    public async Task<int> RevokeAllSessionsForAdminAsync(
        string userId,
        string revokedBy,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        EnsureValidTenant();

        var sessions = await _db.UserSessions
            .Where(s => s.UserId == userId && !s.IsRevoked)
            .ToListAsync(cancellationToken);

        var tenantId = _multiTenantContextAccessor?.MultiTenantContext?.TenantInfo?.Id;
        var revokedAt = _timeProvider.GetUtcNow().UtcDateTime;
        foreach (var session in sessions)
        {
            session.Revoke(revokedAt, revokedBy, reason ?? "Admin requested", tenantId);
        }

        await _db.SaveChangesAsync(cancellationToken);

        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("Admin {AdminId} revoked {Count} sessions for user {UserId}",
                revokedBy, sessions.Count, userId);
        }

        return sessions.Count;
    }

    public async Task<bool> RevokeSessionForAdminAsync(
        Guid sessionId,
        string revokedBy,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        EnsureValidTenant();

        var session = await _db.UserSessions
            .FirstOrDefaultAsync(s => s.Id == sessionId && !s.IsRevoked, cancellationToken);

        if (session is null)
        {
            return false;
        }

        var tenantId = _multiTenantContextAccessor?.MultiTenantContext?.TenantInfo?.Id;
        session.Revoke(_timeProvider.GetUtcNow().UtcDateTime, revokedBy, reason ?? "Admin requested", tenantId);

        await _db.SaveChangesAsync(cancellationToken);

        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("Admin {AdminId} revoked session {SessionId}", revokedBy, sessionId);
        }

        return true;
    }

    public async Task CleanupExpiredSessionsAsync(
        CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var cutoffDate = now.AddDays(-30); // Keep revoked sessions for 30 days for audit
        var deleted = await _db.UserSessions
            .Where(s => s.ExpiresAt < now && s.ExpiresAt < cutoffDate)
            .ExecuteDeleteAsync(cancellationToken);

        if (deleted > 0 && _logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("Cleaned up {Count} expired sessions", deleted);
        }
    }

    private UserSessionDto MapToDto(UserSession session, bool isCurrentSession)
    {
        return new UserSessionDto
        {
            Id = session.Id,
            UserId = session.UserId,
            UserName = session.User?.UserName,
            UserEmail = session.User?.Email,
            IpAddress = session.IpAddress,
            DeviceType = session.DeviceType,
            Browser = session.Browser,
            BrowserVersion = session.BrowserVersion,
            OperatingSystem = session.OperatingSystem,
            OsVersion = session.OsVersion,
            CreatedAt = session.CreatedAt,
            LastActivityAt = session.LastActivityAt,
            ExpiresAt = session.ExpiresAt,
            IsActive = !session.IsRevoked && session.ExpiresAt > _timeProvider.GetUtcNow().UtcDateTime,
            IsCurrentSession = isCurrentSession
        };
    }
}