namespace Boilerplate.BuildingBlocks.Caching;

/// <summary>
/// The catalogue of <b>logical</b> cache keys and tags.
///
/// Nothing here mentions a tenant, and nothing here may: the cache itself prefixes every key and tag
/// with the ambient tenant (<see cref="CacheKeyScope"/>, ADR-0002). A key that carried a tenant id as
/// well would simply double up — <c>t:acme:theme:t:acme</c> — and, worse, would re-open the door to
/// one caller reading another tenant's entry by passing a different id. An architecture test holds
/// this type to it.
/// </summary>
/// <remarks>
/// Keys and tags are persisted to Redis, so a format change silently invalidates every running
/// instance's entries. Treat the strings below as a wire format.
/// </remarks>
public static class CacheKeys
{
    /// <summary>
    /// Well-known tag values for bulk invalidation. Tags are scoped exactly like keys, so
    /// <c>RemoveByTagAsync(Tags.Permissions)</c> clears the calling tenant's permission entries and
    /// nobody else's — there is no cheap cross-tenant eviction.
    /// </summary>
    public static class Tags
    {
        /// <summary>Tag applied to every permission entry.</summary>
        public const string Permissions = "permissions";

        /// <summary>Tag applied to every tenant theme entry.</summary>
        public const string Themes = "themes";

        // There is deliberately no Idempotency tag. Replay entries are written straight to
        // IDistributedCache by IdempotencyEndpointFilter — HybridCache has no get-only probe, and the
        // framed L2 payload it writes is unreadable to a direct reader (#82) — so nothing carries a
        // tag and a tag nobody sets is a bulk invalidation that silently evicts nothing.

        /// <summary>Per-user tag — invalidates all entries scoped to a user within the tenant.</summary>
        public static string User(string userId) => $"user:{userId}";

        // There is deliberately no Tenant(id) tag. Every key and tag already lives in the ambient
        // tenant's partition, so a per-tenant tag would be a no-op at best and, when given someone
        // else's id, a way to evict across the boundary the prefix exists to draw.
    }

    /// <summary>Key for the permission list of a given user, within the ambient tenant.</summary>
    public static string UserPermissions(string userId) => $"perm:u:{userId}";

    /// <summary>Key for the ambient tenant's theme.</summary>
    public const string TenantTheme = "theme";

    /// <summary>
    /// Key for the theme flagged as the default for new tenants. The row it caches lives in the
    /// unfiltered tenant-catalog context — one row, shared by every tenant, not a per-tenant query —
    /// so it is written through <see cref="GlobalHybridCache"/> rather than the tenant-scoped cache.
    /// </summary>
    public const string DefaultTheme = "theme:default";

    /// <summary>
    /// Key for an idempotency replay entry. <paramref name="binding"/> is the filter's hash of who is
    /// asking and what they are asking for — subject (or an anonymous marker), HTTP method and path —
    /// so a client key can only ever replay that caller's own response to that same endpoint; the
    /// tenant comes from the namespace, as it does for every other key here. The client-generated key
    /// stays readable at the tail, where nothing follows it and so nothing it contains can be
    /// arranged to name another partition.
    /// </summary>
    /// <remarks>
    /// Used for both the tenant-scoped and the explicitly global entry: the branch is which cache
    /// namespace the filter scopes it into, not a different key shape.
    /// </remarks>
    public static string IdempotencyEntry(string binding, string key) => $"idem:{binding}:{key}";

    /// <summary>
    /// Key for the impersonation-grant revocation marker, indexed by JWT id. Read on every
    /// authenticated request that carries an act_sub claim — from the JwtBearer
    /// <c>OnTokenValidated</c> hook, which runs before tenant resolution — so this entry lives in
    /// <see cref="GlobalHybridCache"/>. The jti is globally unique and the grant row itself is an
    /// <c>IGlobalEntity</c>, so there is no tenant to scope it to.
    /// </summary>
    public static string ImpersonationGrantStatus(string jti) => $"impgrant:{jti}";
}
