# Background jobs (Hangfire)

`src/BuildingBlocks/Jobs/`. Read before enqueuing or scheduling work.

## Fire-and-forget / scheduled — `IJobService`

Inject `IJobService` (`Jobs/Services/IJobService.cs`) and use it; don't call Hangfire's `BackgroundJob` directly in feature code.

```csharp
jobService.Enqueue(() => mailService.SendAsync(req, CancellationToken.None));   // default queue
jobService.Enqueue("email", () => mailService.SendAsync(req, CancellationToken.None));
jobService.Schedule(() => DoLater(), TimeSpan.FromMinutes(5));
```

Queues: `default`, `email` (5 workers, 30s poll). Storage is `Hangfire.PostgreSql` — `DatabaseOptions.Provider` must be `POSTGRESQL` (ADR-0003), any other value throws at startup.

## Every job is tenant-bound or `[SystemJob]` (ADR-0002)

`AppJobFilter` stamps the **ambient** tenant's Id onto the job at enqueue — the ambient context, never `HttpContext`, so enqueuing from a hosted service or an event handler works the same as from a request. `AppJobActivator` re-reads the full tenant record from the store and opens the tenant **before** the job's DI scope, so a tenant with a dedicated connection string gets its own database. Only the Id travels: the record is never serialized into job storage.

There is no third, silent option:

| | enqueue | run |
|---|---|---|
| unmarked, tenant ambient | tenant Id stamped | runs under that tenant |
| unmarked, no tenant | **throws**, naming the job | — |
| unmarked, no tenant parameter | — | **job fails** |
| `[SystemJob]` | never stamped | runs tenant-less |
| tenant deleted or deactivated | — | **job fails** |

Mark the job class (or the method Hangfire invokes) `[SystemJob]` only when the work genuinely belongs to no tenant — a maintenance sweep, a fan-out, provisioning a tenant that is not usable yet. Today: `PurgeOrphanedFilesJob`, `PurgeDeletedFilesJob`, `AuditRetentionJob`, `TenantExpiryScanJob`, `TenantProvisioningJob`.

## Touching tenant data from a system job — `ITenantScope`

`ITenantScope` (`BuildingBlocks/Shared/Multitenancy/`) is the only way to enter a tenant outside a request. It loads the full `AppTenantInfo` from the store, installs it, and *then* creates the DI scope — that order is the whole point, because a `MultiTenantDbContext` captures its `TenantInfo`, and the tenant's connection string with it, at construction.

```csharp
await tenantScope.RunAsync(tenantId, async (services, ct) =>
{
    var db = services.GetRequiredService<FilesDbContext>();   // built under tenantId
    await db.SaveChangesAsync(ct);
}, ct);

await tenantScope.RunForEachTenantAsync(async (tenant, services, ct) => { … }, ct);   // fan-out
```

Never write `IMultiTenantContextSetter` yourself — an architecture test fails the build if any file outside `AmbientTenantContext` names it.

`RunAsync` is what you want. `Begin` exists for Hangfire's activator alone and is **synchronous on purpose**: the ambient tenant is an `AsyncLocal`, and a write made in the continuation of an `async` method is discarded when that method returns, so an async `Begin` would hand back a scope whose tenant is already gone.

## Recurring jobs — `IRecurringJobManager`

`IJobService` has **no** recurring API. Register recurring jobs in the module's `MapEndpoints` with `IRecurringJobManager.AddOrUpdate<T>(...)`, always `TimeZoneInfo.Utc`:

```csharp
recurringJobs.AddOrUpdate<PurgeOrphanedFilesJob>("files:purge-orphaned",
    j => j.RunAsync(CancellationToken.None), Cron.Hourly(), new() { TimeZone = TimeZoneInfo.Utc });
```

Examples in the tree: `PurgeOrphanedFiles`/`PurgeDeletedFiles` (Files), `AuditRetentionJob` (Auditing), `TenantExpiryScanJob` (Multitenancy).

## Dashboard & config

`/jobs` (`HangfireOptions.Route`), mapped as a routed endpoint after `UseAuthentication`/`UseAuthorization` and gated by `.RequirePermission(SystemPermissions.Hangfire.View)` — a root-only operator permission. There is no dashboard credential: anonymous → 401, signed in without the permission → 403. Hangfire's own `DashboardOptions.Authorization` is empty on purpose, so ASP.NET Core authorization is the single gate. Authentication is bearer-only (no cookie), so plain browser navigation gets 401; the request must carry an `Authorization: Bearer` header.

## Gotchas

- A recurring job is triggered by the scheduler with no tenant in scope, so **every recurring job must be `[SystemJob]`** — otherwise it throws at trigger time. Per-tenant recurring work is a `[SystemJob]` that fans out with `ITenantScope.RunForEachTenantAsync`.
- The two Files purges and the audit retention sweep are `[SystemJob]`s that read whichever database the default connection points at; they do **not** reach a tenant that lives in a dedicated database. Fan out with `ITenantScope` if you need that.
- The DbMigrator registers `NoOpJobService` whose methods **throw** — surfaces any accidental enqueue during migration. Don't enqueue from migration/seed paths.
- A job class is a normal DI-resolved type (scope-per-job via `AppJobActivator`); inject what you need.
