# Database & EF Core conventions

Read before touching entities, DbContexts, migrations, or query filters.

## Entities

- `BaseEntity` — `Id`, `CreatedAt`, `UpdatedAt`, `TenantId`.
- `AggregateRoot` — `BaseEntity` + domain events (`IHasDomainEvents`, `_domainEvents` list).
- Marker interfaces: `IHasTenant`, `IAuditableEntity`, `ISoftDeletable`, `IGlobalEntity`.
- Domain events inherit `DomainEvent` (record: `EventId`, `OccurredOnUtc`, `CorrelationId`, `TenantId`). Integration events implement `IIntegrationEvent`; handlers `IIntegrationEventHandler<T>`.

## Tenant isolation (default-ON)

- `BaseDbContext` auto-applies a tenant query filter to every entity. **Isolation is on by default.**
- Opt out **only** via `IGlobalEntity`. The whole current list is `ImpersonationGrant` (the audit record of an operator acting as someone; revoking one is the kill switch for an impersonation in flight, so it must be reachable from outside the impersonated tenant), `OutboxMessage` and `InboxMessage` (the dispatcher drains them across tenants and each row carries its own `TenantId` for the scope it re-enters). Adding a fourth is a design decision — `Architecture.Tests/TenantIsolationTests` builds every `BaseDbContext` model and fails on an entity that is neither filtered nor marked.
- A subclass DbContext that overrides `OnModelCreating` **must call `base.OnModelCreating(modelBuilder)` LAST**, or the auto-applied filters are lost.
- **Query-filter naming:** SoftDelete filter is *named* (`QueryFilters.SoftDelete`); the tenant filter stays *anonymous* (Finbuckle owns it). Don't rename the tenant filter.

### `IgnoreQueryFilters()` is allow-listed

`Architecture.Tests/IgnoreQueryFiltersAllowListTests` pins **which files may call it and how many times**, plus a second, shorter list of files allowed to use the **bare** form. An unlisted file, a changed count, or an entry whose file is gone all fail.

- **Want deleted rows?** Lift the named filter only: `IgnoreQueryFilters([QueryFilters.SoftDelete])`. The tenant filter stays in force, so the query cannot reach another tenant's rows. This is almost always what a trash view, a restore handler or a purge job actually wants.
- **The bare `IgnoreQueryFilters()` strips the tenant filter too.** Reserve it for genuinely cross-tenant work — a root operator resolving a subject in another tenant — and **re-pin the tenant with an explicit predicate in the same query**. Never rely on the absence of the filter.
- Adding an entry means editing the allow-list *and* saying why at the call site.

## AsNoTracking — and when NOT to

- Read-only queries: add `.AsNoTracking()` (Specifications default to it).
- **Do NOT add `AsNoTracking()` to a read-then-mutate-then-`SaveChanges` query** — the entity must stay tracked or your changes won't persist. The analyzer (AP010) flags these as a smell, but for mutate-and-save flows it is a false positive — leave them tracked.
- `AnyAsync(...)` materializes no entity, so `AsNoTracking()` there is a no-op — skip it.

## Value generation for nav-collection children

A child entity reached **only** through a parent's navigation collection needs `Property(x => x.Id).ValueGeneratedNever()` in its EF config — otherwise EF treats it as `Modified` instead of `Added` and the insert silently misbehaves.

## Migrations

All migrations live in **one** project, `src/Host/Boilerplate.Migrations.PostgreSQL`, organized **per-module by folder** (`Identity/`, `Files/`, …), each with its own `{Module}DbContextModelSnapshot`.

```bash
dotnet ef migrations add {Name} \
  --project src/Host/Boilerplate.Migrations.PostgreSQL \
  --startup-project src/Host/Boilerplate.Api \
  --context {Module}DbContext
```

- **`migrations remove` operates on the snapshot** — run a full build *before* `migrations add` so the snapshot is current, or you can lose the previous migration.
- The DB is **not** migrated at API startup. The `DbMigrator` host is a separate step: `apply` (default), `seed`, `list-pending`; flags `--tenant <id>`, `--catalog-only`, `--seed`. It migrates the tenant catalog first, then the shared module schema once, then seeds per tenant — all serialized by a Postgres advisory lock.
- `dotnet-ef` is pinned in `.config/dotnet-tools.json` — run `dotnet tool restore` first.

## Tests + EF

- Integration tests use Testcontainers (real PostgreSQL) — **Docker must be running**.
- In integration tests, set the Finbuckle tenant context **inline in the same method** as the `UserManager`/`DbContext` call; an awaited-helper set is lost (AsyncLocal) and the tenant query filter NREs.
