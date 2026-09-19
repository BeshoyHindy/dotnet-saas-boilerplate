# Module: Multitenancy

Tenant catalog, provisioning, activation/upgrade, per-tenant theming (Finbuckle.MultiTenant). Foundational — registered early.

**Entities / DbContext:** `AppTenantInfo` (catalog), `TenantProvisioning` + `TenantProvisioningStep`, `TenantTheme`. `TenantDbContext` holds the tenant catalog in the main DB.
**Areas:** CreateTenant, ChangeTenantActivation, UpgradeTenant, Get(Tenants/Status/Migrations), TenantProvisioning (status/retry), TenantTheme (get/update/reset). Full list: `Features/v1/` or `/scalar`.

## Gotchas

- **One strategy, no chain** (ADR-0002) — `TokenOrRouteTenantStrategy` is the only `IMultiTenantStrategy`, and an architecture test fails the build if a header/query/host/base-path/claim/route strategy reappears anywhere under `src/`. Authenticated → the token's `tenant` claim; anonymous → the `{tenant}` route value, but only on endpoints carrying `[TenantFromRoute]` (the `api/v1/tenants/{tenant}/auth/...` group). Stores are unchanged: DistributedCache → EFCoreStore.
- **`UseMultiTenant()` lives in `ConfigureMiddleware`**, not the host, so it runs after `UseRouting()`/`UseAuthentication()` — the strategy needs both the principal and the matched endpoint. Module order 200 keeps it ahead of every module that reads the tenant.
- **Authenticated + no resolvable tenant → 401**, enforced by the guard right after `UseMultiTenant()` (claim missing, blank, or naming a tenant the store doesn't know).
- **There is no root-operator cross-tenant override.** A root caller is pinned to `root` like anyone else; cross-tenant access returns as an audited token exchange. Never reintroduce a caller-supplied tenant input.
- **`ITenantInitialPasswordBuffer`** (singleton) — the tenant admin password is **operator-supplied**, not a constant. `CreateTenantCommandHandler` calls `Store(tenantId, password)` **before** kicking off provisioning; the background seed step `TryConsume`s it (`ConcurrentDictionary`, consume = remove).
- **Provisioning** runs 4 steps (Database → Migrations → Seeding → CacheWarm) via a Hangfire `TenantProvisioningJob`, falling back to inline execution if Hangfire storage is unavailable. **Activation is gated on `Status == Completed`.** The job is `[SystemJob]` — it is work *about* a tenant, enqueued by a root operator, with the target tenant as an argument.
- **`ITenantScope` is the only way to enter a tenant outside a request** (ADR-0002). `RunAsync(tenantId, work)` loads the full `AppTenantInfo` (cache-first — it tries the 60-minute `DistributedCacheStore` before the EF store, same as the HTTP path, and warms the cache on a miss), installs the ambient context, and *then* creates the DI scope — that order is what keeps a per-tenant connection string alive, since a `MultiTenantDbContext` captures `TenantInfo` at construction. `RunForEachTenantAsync` is the fan-out and always reads the EF store, since the cache store can't enumerate. `ITenantService.MigrateTenantAsync`/`SeedTenantAsync`, the migrations health check and query handler, `SqlAuditSink`, the role-permission syncer, jobs and event dispatch all go through it.
- **Only `AmbientTenantContext` may write `IMultiTenantContextSetter`** — an architecture test (`AmbientTenantContextTests`) fails the build on any other production file, because the hand-rolled "create a scope, then set the tenant" pattern it replaces is wrong in every case.
- `ITenantScope.Begin` is **synchronous by design** and exists only for Hangfire's `JobActivator.BeginScope`: the ambient tenant is an `AsyncLocal`, and a write in the continuation of an `async` method is discarded when it returns. Use `RunAsync` everywhere else.

Tenant **isolation** mechanics (default-on filter, `IGlobalEntity` opt-out, `base.OnModelCreating` last) live in `database.md`.
