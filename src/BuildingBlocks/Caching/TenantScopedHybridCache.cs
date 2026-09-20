using Microsoft.Extensions.Caching.Hybrid;

namespace Boilerplate.BuildingBlocks.Caching;

/// <summary>
/// The <see cref="HybridCache"/> every consumer injects. It rewrites every key and every tag to the
/// ambient tenant's partition before handing the call on, so tenant isolation of the cache is a
/// property of the building block rather than of caller discipline (ADR-0002).
/// </summary>
/// <remarks>
/// <para>
/// <b>No ambient tenant throws.</b> Not a fallback tenant, not a shared "global" bucket — a throw
/// that names the key and points at the two legitimate answers (enter the tenant, or declare the
/// entry global via <see cref="GlobalHybridCache"/>). A fallback is how tenant-less callers quietly
/// end up sharing one partition, which is the bug this type removes.
/// </para>
/// <para>
/// <b>Outermost decorator.</b> This wraps <c>ObservableHybridCache</c>, not the other way round, so
/// OpenTelemetry records the <i>physical</i> key — the string you can actually go and look up in
/// Redis, and the one that tells you which tenant a hot key belongs to. The key is an activity tag,
/// not a metric dimension, so making it more specific costs no metric cardinality. The throw above
/// happens before any span opens, which also keeps hit/miss counters free of failed calls.
/// </para>
/// </remarks>
internal sealed class TenantScopedHybridCache : HybridCache
{
    private readonly HybridCache _inner;
    private readonly CacheKeyScope _scope;

    public TenantScopedHybridCache(HybridCache inner, CacheKeyScope scope)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(scope);
        _inner = inner;
        _scope = scope;
    }

    public override ValueTask<T> GetOrCreateAsync<TState, T>(
        string key,
        TState state,
        Func<TState, CancellationToken, ValueTask<T>> factory,
        HybridCacheEntryOptions? options = null,
        IEnumerable<string>? tags = null,
        CancellationToken cancellationToken = default)
        => _inner.GetOrCreateAsync(_scope.TenantKey(key), state, factory, options, ScopeTags(tags), cancellationToken);

    public override ValueTask SetAsync<T>(
        string key,
        T value,
        HybridCacheEntryOptions? options = null,
        IEnumerable<string>? tags = null,
        CancellationToken cancellationToken = default)
        => _inner.SetAsync(_scope.TenantKey(key), value, options, ScopeTags(tags), cancellationToken);

    public override ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default)
        => _inner.RemoveAsync(_scope.TenantKey(key), cancellationToken);

    public override ValueTask RemoveAsync(IEnumerable<string> keys, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        return _inner.RemoveAsync(Map(keys, _scope.TenantKey), cancellationToken);
    }

    public override ValueTask RemoveByTagAsync(string tag, CancellationToken cancellationToken = default)
        => _inner.RemoveByTagAsync(_scope.TenantTag(tag), cancellationToken);

    public override ValueTask RemoveByTagAsync(IEnumerable<string> tags, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tags);
        return _inner.RemoveByTagAsync(Map(tags, _scope.TenantTag), cancellationToken);
    }

    private string[]? ScopeTags(IEnumerable<string>? tags)
        => tags is null ? null : Map(tags, _scope.TenantTag);

    // Materialized on purpose: HybridCache is free to enumerate tags more than once, and a lazy
    // Select would then re-read the ambient tenant on a continuation that may no longer have one.
    private static string[] Map(IEnumerable<string> values, Func<string, string> scope)
        => [.. values.Select(scope)];
}
