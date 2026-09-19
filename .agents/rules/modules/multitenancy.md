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
- **Provisioning** runs 4 steps (Database → Migrations → Seeding → CacheWarm) via a Hangfire `TenantProvisioningJob`, falling back to inline execution if Hangfire storage is unavailable. **Activation is gated on `Status == Completed`.**
- `ITenantService.MigrateTenantAsync`/`SeedTenantAsync` create a fresh scope and set `IMultiTenantContext` **first**, then run the `IDbInitializer`s.

Tenant **isolation** mechanics (default-on filter, `IGlobalEntity` opt-out, `base.OnModelCreating` last) live in `database.md`.
