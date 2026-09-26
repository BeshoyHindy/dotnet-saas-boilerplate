# Performance defaults a first product inherits

Research for [#129](https://github.com/BeshoyHindy/dotnet-saas-boilerplate/issues/129), part of the map [#87](https://github.com/BeshoyHindy/dotnet-saas-boilerplate/issues/87).

- **This repo:** `develop` at `83f43cc`, reviewed on branch `research/performance-defaults`.
- **Method:** read the code under `src/` for EF Core query shape, caching, connection/DbContext
  lifetime, response compression, rate limiting, Hangfire, and API cold-start cost. Every finding
  cites the file and line it rests on. Findings only — no fixes were applied and no fix design is
  proposed; sizes are rough estimates for the decision ticket that graduates from this one.
- **Measurement:** every claim below rests on reading the code and, where cited, official EF
  Core / ASP.NET Core / Hangfire documentation — nothing here was profiled or load-tested. Each
  finding says so explicitly where it matters (e.g. cold-start, worker throughput).
- **Skipped by instruction:** Aspire containers, first compose cost, the `libgssapi` log, the
  per-process idempotency lock, dependency versions, and the object store (recorded known limits).
  The no-L1-backplane cache tradeoff is not one of those — it's this review's own judgment call to
  leave out, since `.agents/rules/caching.md` already documents it as a deliberate, accepted design
  choice rather than an inherited default worth re-litigating here.

Sizes follow this repo's own convention (`upstream-delta.md`): **XS** < 20 lines · **S** < 150 ·
**M** < 500 · **L** beyond that (production code plus tests, rough).

## Summary

| # | Area | Location | Severity | Fix size |
|---|---|---|---|---|
| 1 | Unbounded list endpoint | `src/Modules/Identity/Modules.Identity/Services/UserProfileService.cs:57-59` (`GET /users`) | High | S |
| 2 | Unbounded list endpoint | `src/Modules/Identity/Modules.Identity/Features/v1/Groups/GetGroupMembers/GetGroupMembersQueryHandler.cs:32-50` | Medium | S |
| 3 | Unbounded list endpoint | `src/Modules/Identity/Modules.Identity/Features/v1/Groups/GetGroups/GetGroupsQueryHandler.cs:20-36`, `.../Users/GetUserGroups/GetUserGroupsQueryHandler.cs:32-47` | Low | S |
| 4 | Missing index for a hot query path | `src/Host/Boilerplate.Migrations.PostgreSQL/Files/20260918204109_InitialFiles.cs:47-70` (`FileAssets`) | Medium | S |
| 5 | Unindexable substring search | `SearchUsersQueryHandler.cs:40-45`, `GetGroupsQueryHandler.cs:28-31` (`.ToLower().Contains(term)`) | Medium | S–M |
| 6 | Full-table load for a membership check | `src/Modules/Multitenancy/Modules.Multitenancy/Services/TenantService.cs:150-151,130` — `ExistsWithNameAsync`/deactivation count (`.GetAllAsync().Any(...)`, `.GetAllAsync().Count(...)`) | Low | S |
| 7 | Hangfire worker count fixed, not scaled | `src/BuildingBlocks/Jobs/Extensions.cs:27-33` | Low | S |
| 8 | No ahead-of-time/ReadyToRun publish for cold start | `src/Host/Dockerfile:26-29`, `src/Host/Boilerplate.Api/Boilerplate.Api.csproj` | Low | M |

Counts: **8 findings** — **1 high · 3 medium · 4 low** (severity is for a first product's likely
early scale, not a measured regression — none of these were profiled or load-tested).

## Findings

### 1. `GET /users` loads every user row in the tenant into memory, unpaginated

`src/Modules/Identity/Modules.Identity/Features/v1/Users/GetUsers/GetUsersQueryHandler.cs:8-20` calls
`IUserService.GetListAsync`, which resolves to
`src/Modules/Identity/Modules.Identity/Services/UserProfileService.cs:57-76`:

```csharp
public async Task<List<UserDto>> GetListAsync(CancellationToken cancellationToken)
{
    var users = await userManager.Users.AsNoTracking().ToListAsync(cancellationToken);
    ...
}
```

There is no `.Skip()`/`.Take()`, no page-size parameter, and the contract itself is unbounded:
`GetUsersQuery : IQuery<List<UserDto>>`
(`src/Modules/Identity/Modules.Identity.Contracts/v1/Users/GetUsers/GetUsersQuery.cs:6`). The
endpoint is live and mapped at `GET /users`
(`src/Modules/Identity/Modules.Identity/Features/v1/Users/GetUsers/GetUsersListEndpoint.cs:16-17`).
A **separate, already-paginated** endpoint exists for the same table —
`SearchUsersQueryHandler` (`src/Modules/Identity/Modules.Identity/Features/v1/Users/SearchUsers/SearchUsersQueryHandler.cs`)
uses `PagedResponse<T>`/`ToPagedResponseAsync` (capped at 100,
`src/BuildingBlocks/Persistence/Pagination/PaginationExtensions.cs:12-13`) — so the unbounded one is
not the only option available, just the one still exposed. Neither client calls it: the dashboard's
users page goes through `/api/v1/identity/users/search`
(`clients/dashboard/src/api/identity.ts:37`), and a repo-wide search for the bare
`/api/v1/identity/users` path finds it only in the generated OpenAPI schema
(`clients/dashboard/src/api/schema.d.ts:795`, `clients/console/src/api/schema.d.ts:795`), never in
call-site code in either client. So the safer fix is also the simpler one.

Users is the table most likely to grow past "small" in any real product (every signed-up account is
a row), which is exactly the case EF Core's own performance guidance warns about: loading an entire
table with no limit is the textbook query-shape problem
([Microsoft Learn: "Efficient Querying" — always apply a `Take`/paging when a query's result set size
is unbounded](https://learn.microsoft.com/en-us/ef/core/performance/efficient-querying)).

**Severity: high** — the query itself is cheap per-row (no N+1, `AsNoTracking`), but the endpoint has
no ceiling: response size and query cost both grow linearly and unboundedly with tenant size, and the
route is reachable by any caller holding the view-users permission. **Fix size: S** — either delete
`GetUsers`/`GetUsersListEndpoint` in favour of `SearchUsers` (which already covers the same data,
paginated), or add the same `IPagedQuery`/`PagedResponse<T>` shape to it.

### 2. A group's membership list has no page cap

`src/Modules/Identity/Modules.Identity/Features/v1/Groups/GetGroupMembers/GetGroupMembersQueryHandler.cs:32-50`
joins `UserGroups` to `Users` for one `GroupId` and returns every row via
`IQuery<IEnumerable<GroupMemberDto>>`
(`src/Modules/Identity/Modules.Identity.Contracts/v1/Groups/GetGroupMembers/GetGroupMembersQuery.cs:6`).
The query itself is a single well-formed `Join` (no N+1), but nothing bounds how many members come
back — a group scoped to "everyone" would return the whole tenant's user roster in one response.

**Severity: medium** — groups are an admin/operator surface, not a hot path, but "all users" is a
realistic group in a growing tenant, and this is precisely the "unbounded list endpoint" shape the
review was asked to check for. **Fix size: S** — add `IPagedQuery`/`PagedResponse<T>` the same way
`SearchUsers` and `ListMyFiles` already do.

### 3. Two more list queries with no page cap, lower realistic risk

- `GetGroupsQueryHandler` (`src/Modules/Identity/Modules.Identity/Features/v1/Groups/GetGroups/GetGroupsQueryHandler.cs:20-36`)
  returns every group in the tenant (`IQuery<IEnumerable<GroupDto>>`).
- `GetUserGroupsQueryHandler` (`.../Users/GetUserGroups/GetUserGroupsQueryHandler.cs:32-47`) returns
  every group one user belongs to.

Both are internally well-built (batched `GroupBy`/`ToDictionaryAsync` for member counts and role
names — no per-row database round trip), so this is a pagination-contract gap, not a query-shape
problem. **Severity: low** — the number of groups in a tenant, and the number of groups one user
belongs to, both track headcount roughly logarithmically rather than linearly; unlikely to be a
practical problem for a first product's scale, but still an uncapped contract. **Fix size: S** each,
same pagination shape.

### 4. `FileAssets` has no index that leads with the columns the list queries actually filter by

`src/Host/Boilerplate.Migrations.PostgreSQL/Files/20260918204109_InitialFiles.cs:47-70` creates four
indexes on `FileAssets`:

```
IX_FileAsset_Deletion   (IsDeleted, DeletedOnUtc)
IX_FileAsset_Owner      (OwnerType, OwnerId)
IX_FileAsset_Status     (Status)
UX_FileAsset_StorageKey (StorageKey, TenantId) unique, filtered
```

But the two file-listing queries filter on different columns entirely:

- `ListMyFilesQueryHandler` (`src/Modules/Files/Modules.Files/Features/v1/ListMyFiles/ListMyFilesQueryHandler.cs:32-38`):
  `WHERE CreatedByUserId = @user AND Status = Available ORDER BY CreatedAtUtc DESC` — no index covers
  `CreatedByUserId` at all.
- `ListSharedFilesQueryHandler` (`src/Modules/Files/Modules.Files/Features/v1/ListSharedFiles/ListSharedFilesQueryHandler.cs:31-39`):
  `WHERE Visibility = Public AND Status = Available AND OwnerType IN (...) ORDER BY CreatedAtUtc DESC`
  — `IX_FileAsset_Status` is single-column and doesn't include `Visibility`, `OwnerType`, or the sort
  column.

Every entity in the model is also implicitly filtered by `TenantId` via Finbuckle's
`IsMultiTenant()` (`src/BuildingBlocks/Persistence/TenantIsolationExtensions.cs:29-42`), which widens
only *unique* indexes (`AdjustUniqueIndexes`) — it does not add `TenantId` to a plain lookup index.
That's true across this codebase generically (`Notifications`, `Identity`'s `Groups`, etc. all have
the same shape), and it's usually harmless there: `Notifications.IX_Notifications_User_Read_Created`
leads with `UserId`, and `FileAssets.IX_FileAsset_Owner`/most of `Groups`' single-column indexes lead
with a column that's already selective on its own (a user id, an owner id), so the tenant scan the
query planner does after using the index is already narrow. `AuditRecords` is the one place this
codebase deliberately leads with `TenantId`
(`IX_AuditRecords_Tenant_OccurredAt`, `IX_AuditRecords_Tenant_EventType_OccurredAt` in
`src/Host/Boilerplate.Migrations.PostgreSQL/Audit/20260918204053_InitialAudit.cs:68-81`) — appropriate
there because its own lookup columns (`EventType`, `Source`) are low-cardinality on their own and only
become selective once paired with a tenant. `FileAssets` is the odd one out for a different reason:
`ListMyFilesQueryHandler`'s filter (`CreatedByUserId`) *is* selective, but there is no index at all —
not even a non-tenant-led one — covering it, and `ListSharedFilesQueryHandler`'s filter
(`Visibility`+`Status`+`OwnerType`, none individually selective) is exactly the low-cardinality case
where the existing single-column `IX_FileAsset_Status` doesn't help and a `TenantId`-first composite
would. Both list queries are already paginated (`Skip`/`Take`, capped at 100 — sound), so the query
shape itself is fine; it's the index that's missing. This is squarely the "missing indexes on
tenant-scoped and lookup columns" the review was asked to check per
[PostgreSQL's own advice that a `WHERE`/`ORDER BY` clause needs a matching index or falls back to a
sequential scan](https://www.postgresql.org/docs/current/indexes-multicolumn.html).

**Severity: medium** — with a small number of files per tenant this is invisible; as file counts grow
per user/tenant it becomes an increasingly large sequential (or partially-indexed) scan on every
"my files" / "shared files" page load. **Fix size: S** — one migration adding
`(CreatedByUserId, Status, CreatedAtUtc)` and `(Visibility, Status, OwnerType, CreatedAtUtc)` (or
equivalent) composite indexes.

### 5. `SearchUsers` and `GetGroups` filter with `LOWER(col) LIKE '%term%'`, which no plain index serves

`SearchUsersQueryHandler.cs:40-45`:

```csharp
string term = query.Search.ToLowerInvariant();
users = users.Where(u =>
    (u.FirstName != null && u.FirstName.ToLower().Contains(term)) ||
    (u.LastName != null && u.LastName.ToLower().Contains(term)) ||
    (u.Email != null && u.Email.ToLower().Contains(term)) ||
    (u.UserName != null && u.UserName.ToLower().Contains(term)));
```

`GetGroupsQueryHandler.cs:28-31` does the same shape over `Name`/`Description`. EF Core translates
`.ToLower().Contains(term)` to `LOWER(col) LIKE '%term%'` (a leading-wildcard pattern), which no
B-tree index — including any composite index this review might otherwise recommend for these tables
— can serve; PostgreSQL falls back to a sequential scan over every row the tenant filter leaves in
play. This is not a hypothetical gap: this codebase already solves exactly this problem elsewhere.
`AuditRecords` has `IX_AuditRecords_UserName_trgm` and `IX_AuditRecords_Source_trgm`
(`src/Host/Boilerplate.Migrations.PostgreSQL/Audit/20260918204053_InitialAudit.cs:60-65,87-92`), both
GIN indexes with PostgreSQL's `pg_trgm` extension (`gin_trgm_ops`), which is precisely what makes a
`LIKE '%term%'` scan indexable
([PostgreSQL docs: `pg_trgm` — "GIN and GiST index support for LIKE, ILIKE ... queries"](https://www.postgresql.org/docs/current/pgtrgm.html)).
The pattern is known and applied to one table's search fields, not to `Users` or `Groups`, which are
searched via the exact same query shape.

**Severity: medium** — the users search box is the first place any tenant admin looks for a specific
person, so it's used often; a full scan of `Users` (and, less often, `Groups`) on every keystroke
scales the same badly as any missing index, just with a query shape B-tree indexing genuinely can't
fix. **Fix size: S–M** — add `pg_trgm` GIN indexes on the searched columns, mirroring the
`AuditRecords` migrations already in the tree; S if scoped to `Users.Email`/`UserName` (the most
selective, most-searched columns), M if extended to every searched column on both tables.

### 6. Tenant existence/count checks load the whole tenant catalog into memory

`src/Modules/Multitenancy/Modules.Multitenancy/Services/TenantService.cs`:

```csharp
public async Task<bool> ExistsWithNameAsync(string name, CancellationToken cancellationToken = default) =>
    (await _tenantStore.GetAllAsync().ConfigureAwait(false)).Any(t => t.Name == name);
```

(line 150-151) and, in the deactivation flow (line 130):

```csharp
int tenantCount = (await _tenantStore.GetAllAsync().ConfigureAwait(false)).Count(t => t.IsActive);
```

Both pull every row from Finbuckle's `EFCoreStore<TenantDbContext, AppTenantInfo>` and filter
client-side rather than pushing the predicate to SQL.

**Severity: low** — the tenant catalog is the one table in this system that is expected to stay
small relative to any tenant's own data (one row per customer, not per end user), and both call
sites are admin-only, low-frequency operations (creating/deactivating a tenant), not hot paths.
Flagged because it's a pattern that stops being free if this codebase's tenant count ever becomes
large, and because "load everything, filter in memory" is exactly the shape the review asked to
check for elsewhere. **Fix size: S** — a filtered query against the underlying `DbContext` instead of
the full `IMultiTenantStore.GetAllAsync()`.

### 7. Hangfire worker count is a fixed `5`, not scaled to the host

`src/BuildingBlocks/Jobs/Extensions.cs:27-33`:

```csharp
services.AddHangfireServer(options =>
{
    options.HeartbeatInterval = TimeSpan.FromSeconds(30);
    options.Queues = ["default", "email"];
    options.WorkerCount = 5;
    options.SchedulePollingInterval = TimeSpan.FromSeconds(30);
});
```

Hangfire's own documentation gives the out-of-the-box `WorkerCount` default as
`Environment.ProcessorCount * 5`
([Hangfire docs: "Configuring Degree of Parallelism"](https://docs.hangfire.io/en/latest/background-processing/configuring-degree-of-parallelism.html)),
i.e. it scales with the host rather than being a fixed number. (This review confirmed the installed
`Hangfire.Core` 1.8.23 binary does read `Environment.ProcessorCount` internally — a decompiled-symbol
check, not a value dump — and takes the multiplier and any cap from the docs page, not from
disassembly.) Here it is pinned to `5` unconditionally, so a host with more cores gets no extra background
throughput from that resource, and a very small host isn't intentionally constrained below it either
— it's simply not wired to the environment at all. `SchedulePollingInterval` is also set to 30s,
double Hangfire's own 15s default, so recurring-job triggering (not job pickup — that's queue-based
and near-instant) lags by up to 15s more than the out-of-the-box behaviour.

**Severity: low** — 5 workers across two queues (`default`, `email`) is plausible for a first
product's job volume, and this is a documented, not accidental, choice
(`.agents/rules/jobs.md`: "Queues: `default`, `email` (5 workers, 30s poll)"). Flagged because it's a
performance *default* that doesn't scale with the host, which is exactly what this review was asked
to check, not because a concrete problem was observed. This rests on reading the code and Hangfire's
documented defaults, not on a load test. **Fix size: S** — bind `WorkerCount` from configuration with
a sane environment-derived default.

### 8. Cold start: JIT-only publish, no ReadyToRun/AOT

`src/Host/Dockerfile:26-29` publishes with a plain `dotnet publish -c Release`, and
`src/Host/Boilerplate.Api/Boilerplate.Api.csproj` sets no `PublishReadyToRun`,
`TieredPGO`/`TieredCompilationQuickJitForLoops` tuning, or `PublishAot`. Every method the API
executes on its first requests — including the reflection-driven module/validator registration in
`src/BuildingBlocks/Web/Modules/ModuleLoader.cs:26-49` (`AddValidatorsFromAssemblies` scans every
module assembly with `Assembly.GetTypes()`) — is JIT-compiled from IL rather than pre-compiled.
Microsoft's own ReadyToRun documentation states this directly: "R2R binaries improve startup
performance by reducing the amount of work the just-in-time (JIT) compiler needs to do as your
application loads... The startup improvement discussed here applies not only to application startup,
but also to the first use of any code in the application. For instance, ReadyToRun can be used to
reduce the response latency of the first use of Web API in an ASP.NET application."
([Microsoft Learn: "ReadyToRun deployment overview"](https://learn.microsoft.com/en-us/dotnet/core/deploying/ready-to-run)).
Mediator's source-generated handlers (`Mediator.SourceGenerator` in
`src/Host/Boilerplate.Api/Boilerplate.Api.csproj`) already avoid the reflection-heavy alternative for
that one piece, which is the sound counter-example: the module/validator scan at
`ModuleLoader.cs:26-49` is the one place still paying the AppDomain-assembly-reflection cost this
review was asked to look for.

**Severity: low** — this is a one-time-per-process-start cost, not a per-request one, and Dokploy's
deployment shape (long-lived containers, not scale-to-zero) means it's paid rarely relative to
request volume; the "first compose cost" already on the map's known-limits list covers the adjacent,
larger local-dev cost. Flagged separately because it's a genuine, unaddressed contributor to restart
latency (deploys, crash recovery, autoscaling if ever added) that the ticket specifically asked about
under "API cold-start cost." This rests on reading the build configuration and general ASP.NET Core
guidance, not a measured startup-time number — no cold-start benchmark was run. **Fix size: M** —
enabling `PublishReadyToRun` (needs a runtime identifier per target, so it's a Dockerfile and csproj
change, not a one-line flag) is the contained option; Native AOT is a much larger change given
reflection-based EF Core/ASP.NET Core usage throughout and is out of scope for an estimate this size.

## Checked and sound

- **Tracking behaviour.** `Specification<T>` defaults `AsNoTracking = true`
  (`src/BuildingBlocks/Persistence/Specifications/Specification.cs:19-21`), and every read-only query
  handler outside the specification pattern calls `.AsNoTracking()` explicitly (verified by scanning
  every `Get*`/`List*`/`Search*` query handler under `src/Modules`). No accidental tracked reads found.
- **Pagination caps that exist are real caps.** `PaginationExtensions.ToPagedResponseAsync`
  (`src/BuildingBlocks/Persistence/Pagination/PaginationExtensions.cs:12-13,32-36`) clamps page size to
  100 regardless of what the caller asks for, and `ListMyFiles`/`ListSharedFiles`
  (`.../Features/v1/ListMyFiles/ListMyFilesQueryHandler.cs:29-30`,
  `.../ListSharedFiles/ListSharedFilesQueryHandler.cs:28-29`) clamp their own manual paging the same
  way. `GetAuditSummary` independently caps its date window at 90 days
  (`src/Modules/Auditing/Modules.Auditing/Features/v1/GetAuditSummary/GetAuditSummaryQueryHandler.cs:16-17,122-125`).
- **DbContext/connection lifetime.** `AddHeroDbContext<TContext>` (scoped, ASP.NET Core default) and
  `ScopedDbConnectionProvider` (`src/BuildingBlocks/Persistence/ScopedDbConnectionProvider.cs`) hand
  EF Core an *unopened, unowned* `DbConnection`, so EF's normal open-per-operation/return-to-pool
  behaviour is unchanged; the only thing shared across a scope's several module `DbContext`s is the
  connection *object* (needed for cross-context transaction enlistment), not an open connection held
  for the request's lifetime. This means a request touching several modules still consumes only one
  pooled Npgsql connection, not one per module.
- **Tenant resolution is cached, not a per-request DB hit.**
  `src/Modules/Multitenancy/Modules.Multitenancy/MultitenancyModule.cs:91-115` wires
  `.WithDistributedCacheStore(TimeSpan.FromMinutes(60))` ahead of the EF store, and
  `OnTenantResolveCompleted` backfills the cache on a store hit — every request after the first for a
  given tenant resolves from cache.
- **Cache design.** `HybridCache` (stampede-protected `GetOrCreateAsync`), tenant-prefixed keys/tags
  enforced by `CacheKeyScope`/`CacheKeys` (`src/BuildingBlocks/Caching/`), and tag-scoped invalidation
  (`RemoveByTagAsync` cannot cross a tenant boundary) are all sound and already documented in
  `.agents/rules/caching.md`.
- **Response compression.** Brotli + Gzip, `EnableForHttps = true`, Brotli level pinned to `Fastest`
  to bound the CPU/latency tradeoff (`src/BuildingBlocks/Web/Extensions.cs:53-60`).
- **Rate limiting.** Chained per-tenant/per-user/per-IP fixed-window limiters plus a tighter `auth`
  policy (`src/BuildingBlocks/Web/RateLimiting/Extensions.cs`), health-check paths exempted, and the
  real client IP is preserved through `ForwardedHeaders` configuration
  (`src/BuildingBlocks/Web/Security/ConfigureForwardedHeaders.cs`) — no LB-IP-collapsing false-positive
  risk found.
- **Outbox dispatch is indexed and claim-based, not a scan.** `IX_OutboxMessages_Pending`
  (`src/Host/Boilerplate.Migrations.PostgreSQL/Eventing/20260918204101_InitialEventing.cs:57-61`)
  covers `(IsDead, ProcessedOnUtc, ClaimedUntilUtc, CreatedOnUtc)`, and claiming uses
  `SELECT ... FOR UPDATE SKIP LOCKED` (`src/BuildingBlocks/Eventing/Outbox/EfCoreOutboxStore.cs:~91-107`)
  so multiple dispatcher instances don't block each other or double-process a row.
- **No N+1 query patterns found** in the `foreach`-containing handlers surveyed
  (`GetGroupsQueryHandler`, `GetUserGroupsQueryHandler`, `GetGroupMembersQueryHandler`,
  `AddUsersToGroupCommandHandler`, `ListMyFilesQueryHandler`, `ListSharedFilesQueryHandler`) — each
  either batches its follow-up lookups (`GroupBy`/`ToDictionaryAsync`, `Join`) or does purely local,
  no-round-trip work per row (URL signing).
