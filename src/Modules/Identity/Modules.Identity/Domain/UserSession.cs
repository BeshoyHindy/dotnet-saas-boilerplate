using Boilerplate.BuildingBlocks.Core.Domain;
using Boilerplate.Modules.Identity.Domain.Events;

namespace Boilerplate.Modules.Identity.Domain;

/// <summary>
/// One row per signed-in device, tenant-isolated (ADR-0002). The row *is* the refresh token:
/// the token itself is never stored, only <see cref="RefreshTokenHash"/>. Rotation moves the
/// spent hash to <see cref="PreviousTokenHash"/> so a replay is recognisable and can burn the
/// whole session, and <see cref="SecurityStamp"/> pins the session to the credential state it
/// was minted against — a password change rotates the stamp and kills every session with it.
/// </summary>
public class UserSession : IHasDomainEvents
{
    private readonly List<IDomainEvent> _domainEvents = [];

    public Guid Id { get; private set; }
    public string UserId { get; private set; } = default!;
    public string RefreshTokenHash { get; private set; } = default!;

    /// <summary>
    /// The hash this session rotated away from. A hit here means the token was replayed.
    /// Only ever written by the rotation <c>ExecuteUpdate</c>, hence <c>init</c> (EF materialization)
    /// rather than a domain mutator that nothing would call.
    /// </summary>
    public string? PreviousTokenHash { get; init; }

    /// <summary>The user's ASP.NET Identity security stamp at the moment this session was created.</summary>
    public string SecurityStamp { get; private set; } = default!;

    public string IpAddress { get; private set; } = default!;
    public string UserAgent { get; private set; } = default!;
    public string? DeviceType { get; private set; }
    public string? Browser { get; private set; }
    public string? BrowserVersion { get; private set; }
    public string? OperatingSystem { get; private set; }
    public string? OsVersion { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime LastActivityAt { get; private set; }
    public DateTime ExpiresAt { get; private set; }
    public bool IsRevoked { get; private set; }
    public DateTime? RevokedAt { get; private set; }
    public string? RevokedBy { get; private set; }
    public string? RevokedReason { get; private set; }

    // Navigation property (init for EF Core materialization)
    public virtual AppUser? User { get; init; }

    // IHasDomainEvents implementation
    public IReadOnlyCollection<IDomainEvent> DomainEvents => _domainEvents.AsReadOnly();
    public void ClearDomainEvents() => _domainEvents.Clear();
    private void AddDomainEvent(IDomainEvent domainEvent) => _domainEvents.Add(domainEvent);

    private UserSession() { } // EF Core

    public static UserSession Create(
        string userId,
        string refreshTokenHash,
        string securityStamp,
        string ipAddress,
        string userAgent,
        DateTime createdAt,
        DateTime expiresAt,
        string? deviceType = null,
        string? browser = null,
        string? browserVersion = null,
        string? operatingSystem = null,
        string? osVersion = null)
    {
        return new UserSession
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            RefreshTokenHash = refreshTokenHash,
            SecurityStamp = securityStamp,
            IpAddress = ipAddress,
            UserAgent = userAgent,
            DeviceType = deviceType,
            Browser = browser,
            BrowserVersion = browserVersion,
            OperatingSystem = operatingSystem,
            OsVersion = osVersion,
            CreatedAt = createdAt,
            LastActivityAt = createdAt,
            ExpiresAt = expiresAt
        };
    }

    public void Revoke(DateTime revokedAt, string? revokedBy = null, string? reason = null, string? tenantId = null)
    {
        if (IsRevoked) return;
        IsRevoked = true;
        RevokedAt = revokedAt;
        RevokedBy = revokedBy;
        RevokedReason = reason;

        AddDomainEvent(SessionRevokedEvent.Create(
            userId: UserId,
            sessionId: Id,
            revokedBy: revokedBy,
            reason: reason,
            tenantId: tenantId));
    }
}
