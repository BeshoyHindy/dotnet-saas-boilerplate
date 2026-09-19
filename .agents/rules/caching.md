# Caching

`src/BuildingBlocks/Caching/`. Read before adding cached reads or invalidation.

## The rule

**Cache keys are tenant-prefixed by the building block, not by caller convention** (ADR-0002).

Injecting `HybridCache` gets you the tenant-scoped cache. It rewrites every key *and every tag* to
`t:{ambientTenantId}:{name}` before touching the real cache, for all six overloads. You write logical
names — `"theme"`, `"perm:u:{userId}"`, `"permissions"` — and never the tenant. Adding it yourself
doubles the prefix and hands the caller a way to name somebody else's partition.

No ambient tenant → **throw**, naming the key and the way out. Never a fallback tenant: a fallback is
how every tenant-less caller quietly ends up sharing one bucket.

## What's registered

`AddHeroCaching(config)` registers **`HybridCache`** (L1 in-memory + optional L2 Redis) wrapped in
two decorators: telemetry innermost (so OTel records the *physical* key you can look up in Redis),
tenant scoping outermost.

- `CachingOptions.Redis` empty → in-memory only (dev fallback). Set → a **single shared
  `ConnectionMultiplexer`** (singleton `IConnectionMultiplexer`) backs both the L2 cache and the
  DataProtection key ring.
- Defaults: total expiration 1h, L1 (local) expiration 2min (`CachingOptions`).
- The block learns the ambient tenant through **`ICacheTenantAccessor`**, which it declares and the
  Multitenancy module implements (`FinbuckleCacheTenantAccessor`) — a building block may not
  reference a module. Same seam as `IEventTenantScope` → `FinbuckleEventTenantScope`.
- A host composed **without** multitenancy says so: `AddHeroCaching(config, singleTenant: true)`.
  Without either, resolving the cache throws with both options spelled out. There is no default.

## Pattern

```csharp
var perms = await cache.GetOrCreateAsync(
    CacheKeys.UserPermissions(userId),     // logical — no tenant in it
    async ct => await LoadPermissionsAsync(userId, ct),
    tags: [CacheKeys.Tags.Permissions],    // logical — scoped for you
    cancellationToken: ct);
```

- **Keys & tags live in `CacheKeys.cs`**, tenant-free. Existing: `UserPermissions(userId)`,
  `TenantTheme`, `DefaultTheme`, `IdempotencyEntry(key)`, `GlobalIdempotencyEntry(subject, key)`,
  `ImpersonationGrantStatus(jti)`; tags `Permissions`, `Themes`, `Idempotency`, `User(id)`.
  An architecture test rejects a `CacheKeys` member that takes a tenant, and rejects an inline key or
  tag string at any call site outside the block.
- Invalidate with `RemoveAsync(key)` or `RemoveByTagAsync(tag)` in the relevant mutation handler.
- `GetOrCreateAsync` gives **stampede protection** for free (factory runs once per key).

## Tags are scoped too

`RemoveByTagAsync("permissions")` evicts `t:{you}:permissions` — your tenant's entries and nobody
else's. That is deliberate: before this, one tenant's role change flushed every tenant's permission
cache, which is a cheap cross-tenant DoS as well as a correctness problem. There is no wildcard that
crosses the boundary.

Cross-tenant invalidation is *per tenant*, run under `ITenantScope.RunAsync` — which is how
`RolePermissionSyncHostedService` already drives `RolePermissionSyncer`.

## Genuinely global entries

Inject **`GlobalHybridCache`**. Keys and tags land in the `g:` namespace, disjoint from every
tenant's `t:{id}:`, and it works with no tenant at all. The constructor parameter is the point: a
reviewer can see the claim "this belongs to no tenant" without reading the method.

Use it when both are true: the data is not a tenant's, **and** the read can happen with no tenant
established. Today that is the impersonation-grant revocation marker — read from the JwtBearer
`OnTokenValidated` hook, which runs *before* tenant resolution, and backing an `IGlobalEntity` keyed
by a globally unique `jti` — plus the idempotency filter's tenant-less branch.

## Reaching another tenant's entries

One mechanism: **`ITenantScope.RunAsync(tenantId, …)`**. Enter the tenant, then use the cache
normally. There is deliberately no `ForTenant(id)` API on the block — that is the "pass someone
else's id" shape ADR-0002 removes.

`TenantThemeService` still takes a `tenantId` argument (it is on a module contract) but now checks it
against the ambient tenant and throws if they differ, so the argument cannot silently file an entry
under the wrong tenant.

## The physical key, for the one caller that needs it

`CacheKeyScope` is the single source of truth for the physical layout: `TenantKey(logical)`,
`TenantTag(logical)`, the static `GlobalKey`/`GlobalTag`, plus `HasTenant`/`AmbientTenantId` for
deciding between the two caches. Ask it — don't rebuild the format.

`IdempotencyEndpointFilter` is the only caller: HybridCache has no get-only probe
(dotnet/aspnetcore#57191), so it reads L2 by key and must name the physical one. An architecture test
keeps `IDistributedCache` to a stale-failing allow-list (that probe, and the Redis health check).

## Gotchas

- **No L1 backplane.** `RemoveByTagAsync` on one node does **not** evict L1 on peer nodes —
  cross-node staleness is bounded only by the 2-min local expiration. Don't rely on instant
  cross-node invalidation; keep local expiration short for hot, mutable data.
- **HybridCache ignores `MemoryDistributedCache` as an L2** (it would only duplicate L1). So with no
  Redis configured — including the integration test host — nothing reaches L2 at all, and anything
  that reads L2 directly sees nothing.
- **HybridCache does not write bare JSON to L2.** It writes a framed payload (version byte, expiry,
  key, tags, then the value). Anything reading those bytes directly has to account for that.
- Don't reach for `IDistributedCache` directly — it skips the tenant prefix, the telemetry and the
  tag bookkeeping. The allow-list above is the whole set of exceptions.

## Related, tracked separately

Storage paths are not yet tenant-prefixed by the building block — that is #78.
