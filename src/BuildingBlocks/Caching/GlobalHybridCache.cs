using Microsoft.Extensions.Caching.Hybrid;

namespace Boilerplate.BuildingBlocks.Caching;

/// <summary>
/// The explicit, named way to cache something that is genuinely platform-wide.
///
/// Injecting <see cref="HybridCache"/> gets you the tenant-scoped cache; injecting
/// <see cref="GlobalHybridCache"/> is a statement — "this entry belongs to no tenant" — that a
/// reviewer can see at the constructor. There is no third option and no implicit fallback, so an
/// entry is global because someone decided it was, not because a tenant happened to be missing.
/// </summary>
/// <remarks>
/// <para>
/// Keys and tags land in the <c>g:</c> namespace, disjoint from every tenant's <c>t:{id}:</c> one,
/// so a global entry can never be read or evicted through a tenant-scoped call and vice versa.
/// </para>
/// <para>
/// Entries in the kit that qualify today: the impersonation-grant revocation marker (keyed by a
/// globally unique <c>jti</c>, read from the JWT <c>OnTokenValidated</c> hook which runs <i>before</i>
/// tenant resolution, and backing an <c>IGlobalEntity</c>), the defensive tenant-less branch of the
/// idempotency filter, and the default-theme row (<c>TenantThemeService</c>) — a single platform-wide
/// record read from the unfiltered tenant-catalog context, not a per-tenant query. Reach for this
/// type when either of two things is true of your entry: the data is not a tenant's, or the read can
/// happen with no tenant established.
/// </para>
/// </remarks>
public sealed class GlobalHybridCache : HybridCache
{
    private readonly HybridCache _inner;

    internal GlobalHybridCache(HybridCache inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
    }

    /// <inheritdoc />
    public override ValueTask<T> GetOrCreateAsync<TState, T>(
        string key,
        TState state,
        Func<TState, CancellationToken, ValueTask<T>> factory,
        HybridCacheEntryOptions? options = null,
        IEnumerable<string>? tags = null,
        CancellationToken cancellationToken = default)
        => _inner.GetOrCreateAsync(CacheKeyScope.GlobalKey(key), state, factory, options, ScopeTags(tags), cancellationToken);

    /// <inheritdoc />
    public override ValueTask SetAsync<T>(
        string key,
        T value,
        HybridCacheEntryOptions? options = null,
        IEnumerable<string>? tags = null,
        CancellationToken cancellationToken = default)
        => _inner.SetAsync(CacheKeyScope.GlobalKey(key), value, options, ScopeTags(tags), cancellationToken);

    /// <inheritdoc />
    public override ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default)
        => _inner.RemoveAsync(CacheKeyScope.GlobalKey(key), cancellationToken);

    /// <inheritdoc />
    public override ValueTask RemoveAsync(IEnumerable<string> keys, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        return _inner.RemoveAsync(Map(keys), cancellationToken);
    }

    /// <inheritdoc />
    public override ValueTask RemoveByTagAsync(string tag, CancellationToken cancellationToken = default)
        => _inner.RemoveByTagAsync(CacheKeyScope.GlobalTag(tag), cancellationToken);

    /// <inheritdoc />
    public override ValueTask RemoveByTagAsync(IEnumerable<string> tags, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tags);
        return _inner.RemoveByTagAsync(Map(tags), cancellationToken);
    }

    private static string[]? ScopeTags(IEnumerable<string>? tags)
        => tags is null ? null : Map(tags);

    private static string[] Map(IEnumerable<string> values)
        => [.. values.Select(CacheKeyScope.GlobalKey)];
}
