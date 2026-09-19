namespace Boilerplate.BuildingBlocks.Caching;

/// <summary>
/// The single source of truth for the physical layout of a cache key or tag.
///
/// Callers write <b>logical</b> names — <c>"theme"</c>, <c>"perm:u:{userId}"</c>, <c>"permissions"</c>.
/// The cache writes <b>physical</b> ones — <c>"t:acme:theme"</c>, <c>"g:impgrant:{jti}"</c>. Nobody
/// outside this block composes that prefix: <see cref="TenantScopedHybridCache"/>,
/// <see cref="GlobalHybridCache"/> and the one caller that must read L2 by key (the idempotency
/// probe-read in <c>IdempotencyEndpointFilter</c>) all come through here, so there is exactly one
/// format and it cannot drift between writer and reader.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why two namespaces.</b> A tenant id matches <c>^[a-z0-9][a-z0-9-]{1,62}$</c> — no colon, no
/// underscore — so <c>t:{tenantId}:{logical}</c> parses unambiguously and a tenant can never be
/// named such that its partition collides with the <c>g:</c> one. Every physical name starts with a
/// namespace, so there is no unqualified key left for a caller to squat on.
/// </para>
/// <para>
/// Tags are scoped exactly like keys, which is the point: <c>RemoveByTagAsync("permissions")</c>
/// from tenant A evicts <c>t:a:permissions</c> and cannot reach tenant B's entries. Cross-tenant
/// invalidation is not something a caller stumbles into — it is entering each tenant through
/// <c>ITenantScope.RunAsync</c>, or declaring the entry global in the first place.
/// </para>
/// </remarks>
public sealed class CacheKeyScope
{
    /// <summary>Namespace of every tenant-scoped key and tag: <c>t:{tenantId}:{logical}</c>.</summary>
    public const string TenantNamespace = "t";

    /// <summary>Namespace of every explicitly global key and tag: <c>g:{logical}</c>.</summary>
    public const string GlobalNamespace = "g";

    private readonly ICacheTenantAccessor _tenantAccessor;

    /// <summary>Creates a scope over <paramref name="tenantAccessor"/>.</summary>
    public CacheKeyScope(ICacheTenantAccessor tenantAccessor)
    {
        ArgumentNullException.ThrowIfNull(tenantAccessor);
        _tenantAccessor = tenantAccessor;
    }

    /// <summary>
    /// The tenant the tenant-scoped cache would use right now, or <see langword="null"/> when there
    /// is no tenant on this async flow. Use it to <i>decide</i> between the tenant cache and
    /// <see cref="GlobalHybridCache"/>; never to build a key by hand.
    /// </summary>
    public string? AmbientTenantId
    {
        get
        {
            var tenantId = _tenantAccessor.TenantId;
            return string.IsNullOrWhiteSpace(tenantId) ? null : tenantId;
        }
    }

    /// <summary>True when a tenant is established on this async flow.</summary>
    public bool HasTenant => AmbientTenantId is not null;

    /// <summary>
    /// The physical key the tenant-scoped cache writes for <paramref name="logicalKey"/> right now.
    /// </summary>
    /// <exception cref="InvalidOperationException">No tenant is ambient.</exception>
    public string TenantKey(string logicalKey) => Scope(logicalKey, "key");

    /// <summary>
    /// The physical tag the tenant-scoped cache writes for <paramref name="logicalTag"/> right now.
    /// </summary>
    /// <exception cref="InvalidOperationException">No tenant is ambient.</exception>
    public string TenantTag(string logicalTag) => Scope(logicalTag, "tag");

    /// <summary>The physical key <see cref="GlobalHybridCache"/> writes for <paramref name="logicalKey"/>.</summary>
    public static string GlobalKey(string logicalKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logicalKey);
        return $"{GlobalNamespace}:{logicalKey}";
    }

    /// <summary>The physical tag <see cref="GlobalHybridCache"/> writes for <paramref name="logicalTag"/>.</summary>
    public static string GlobalTag(string logicalTag) => GlobalKey(logicalTag);

    private string Scope(string logical, string kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logical);

        var tenantId = AmbientTenantId ?? throw new InvalidOperationException(
            $"Cache {kind} '{logical}' was used with no ambient tenant. Cache entries are tenant-scoped " +
            "by the Caching building block (ADR-0002), and there is no implicit fallback tenant. Either " +
            "enter the tenant first — ITenantScope.RunAsync is the one way to do that outside a request — " +
            "or, if this entry is genuinely tenant-less, inject GlobalHybridCache and say so explicitly.");

        return $"{TenantNamespace}:{tenantId}:{logical}";
    }
}
