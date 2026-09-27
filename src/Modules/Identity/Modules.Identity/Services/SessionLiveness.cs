using Boilerplate.BuildingBlocks.Shared.Multitenancy;
using Boilerplate.Modules.Identity.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace Boilerplate.Modules.Identity.Services;

/// <summary>
/// Answers "is the session this access token names still live?" for the JwtBearer
/// <c>OnTokenValidated</c> hook, so a revoked, expired or unknown session is refused with 401 on the
/// next request rather than when the access token expires (#118).
/// </summary>
/// <remarks>
/// <para>
/// <b>Cost.</b> The answer — live or dead — is cached in process for <see cref="CacheDuration"/> per
/// (tenant, session). An authenticated request is therefore a dictionary lookup, and a session costs at
/// most one tenant-filtered read per instance per <see cref="CacheDuration"/>. A cached live answer
/// still re-checks the session's own expiry against the clock on every hit, and a dead one is cached
/// too, so a revoked token replayed in a loop is not a database read per request.
/// </para>
/// <para>
/// <b>How immediate revocation is.</b> <see cref="SessionService"/> calls <see cref="MarkRevoked"/>
/// after every revocation commits, so on the instance that handled the revoke the session's very next
/// request is refused. Every <i>other</i> instance keeps whatever it cached before and refuses the
/// session once that entry lapses: <b>at most <see cref="CacheDuration"/> after the revoke commits</b>.
/// There is no cross-instance invalidation. On the default single-replica stack the bound is zero.
/// </para>
/// <para>
/// <b>Why a private <see cref="MemoryCache"/> and not <c>HybridCache</c>.</b> The hook runs before
/// Finbuckle has resolved the request's tenant, so the tenant-scoped <c>HybridCache</c> has no tenant to
/// prefix with, and <c>GlobalHybridCache</c> is reserved for data that belongs to no tenant — a session
/// is a tenant's row. A Redis L2 would not tighten the cross-instance bound either: HybridCache has no
/// L1 backplane, so peers would still serve their local copy for its local expiration. The cache here
/// is owned by this class, size-bounded, and keyed by a (tenant, session) value rather than a string,
/// so nothing else can read it and no key can land in a shared namespace.
/// </para>
/// <para>
/// <b>Tenant.</b> <c>UserSession</c> is tenant-filtered, so a miss enters the token's tenant through
/// <see cref="ITenantScope.RunAsync{TResult}"/> — the sanctioned way into a tenant outside Finbuckle's
/// own resolution — and reads with the filter on. A tenant the store does not know counts as an
/// unknown session. Whether the tenant is <i>active</i> is deliberately not decided here: the
/// deactivated-tenant guard answers that with 403 once the tenant is resolved.
/// </para>
/// </remarks>
public sealed class SessionLiveness : IDisposable
{
    /// <summary>
    /// How long one answer is trusted. This is the cross-instance revocation bound; keep it well
    /// below <c>JwtOptions.AccessTokenMinutes</c> or the check buys nothing on a multi-replica stack.
    /// </summary>
    public static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(30);

    // One entry per recently seen session per instance. Past this, MemoryCache compacts — an evicted
    // entry just costs one more read, and an evicted revocation marker is re-read as revoked.
    private const long MaxEntries = 100_000;

    private static readonly SessionState RevokedMarker = new(IsRevoked: true, ExpiresAt: DateTime.MinValue);

    private readonly ITenantScope _tenantScope;
    private readonly TimeProvider _timeProvider;
    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = MaxEntries });

    // Serialises the two writers — a finished lookup and a revocation — so a lookup that read the row
    // before a revoke committed cannot overwrite the revocation marker with the live state it saw.
    private readonly Lock _gate = new();

    public SessionLiveness(ITenantScope tenantScope, TimeProvider timeProvider)
    {
        _tenantScope = tenantScope;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// True when <paramref name="sessionId"/> is a session of <paramref name="tenantId"/> that is
    /// neither revoked nor expired. Unknown sessions and unknown tenants are not live.
    /// </summary>
    public async ValueTask<bool> IsLiveAsync(string tenantId, Guid sessionId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        var key = new Key(tenantId, sessionId);

        if (_cache.TryGetValue(key, out Entry? cached)
            && cached is not null
            && cached.FreshUntil > _timeProvider.GetUtcNow())
        {
            return IsLive(cached.State);
        }

        SessionState? state;
        try
        {
            state = await _tenantScope
                .RunAsync<SessionState?>(
                    tenantId,
                    (services, ct) => LoadAsync(services, sessionId, ct),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (UnknownTenantException)
        {
            state = null;
        }

        lock (_gate)
        {
            // A revocation that landed while this read was in flight wins: revocation is terminal,
            // so a revoked entry is never replaced by anything this instance read earlier.
            if (_cache.TryGetValue(key, out Entry? current) && current?.State is { IsRevoked: true })
            {
                return false;
            }

            Store(key, state);
        }

        return IsLive(state);
    }

    /// <summary>
    /// Records that a session was revoked, so the next <see cref="IsLiveAsync"/> on this instance
    /// refuses it without waiting for the cached answer to lapse. Call it after the revocation has
    /// committed. It replaces the entry with a revocation marker rather than removing it, which is
    /// what stops a lookup already in flight from re-caching the pre-revoke row.
    /// </summary>
    public void MarkRevoked(string tenantId, Guid sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        lock (_gate)
        {
            Store(new Key(tenantId, sessionId), RevokedMarker);
        }
    }

    public void Dispose() => _cache.Dispose();

    private bool IsLive(SessionState? state) =>
        state is { IsRevoked: false } && state.ExpiresAt > _timeProvider.GetUtcNow().UtcDateTime;

    private void Store(Key key, SessionState? state)
    {
        var entry = new Entry(state, _timeProvider.GetUtcNow() + CacheDuration);

        // Freshness is decided by Entry.FreshUntil against the injected clock; this expiration only
        // lets MemoryCache reclaim the slot.
        _cache.Set(key, entry, new MemoryCacheEntryOptions
        {
            Size = 1,
            AbsoluteExpirationRelativeToNow = CacheDuration,
        });
    }

    private static Task<SessionState?> LoadAsync(IServiceProvider services, Guid sessionId, CancellationToken ct) =>
        services.GetRequiredService<IdentityDbContext>()
            .UserSessions
            .AsNoTracking()
            .Where(s => s.Id == sessionId)
            .Select(s => new SessionState(s.IsRevoked, s.ExpiresAt))
            .FirstOrDefaultAsync(ct);

    private readonly record struct Key(string TenantId, Guid SessionId);

    private sealed record Entry(SessionState? State, DateTimeOffset FreshUntil);
}

/// <summary>The two columns of a <c>UserSession</c> row that decide whether it is live.</summary>
internal sealed record SessionState(bool IsRevoked, DateTime ExpiresAt);
