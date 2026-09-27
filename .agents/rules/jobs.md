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

`AppJobFilter` stamps the **ambient** tenant's Id onto the job at enqueue — the ambient context, never `HttpContext`, so enqueuing from a hosted service or an event handler works the same as from a request. `AppJobActivator` loads the full tenant record through `ITenantScope.GetTenantAsync` (cache-first, same as the HTTP path) and opens the tenant **before** the job's DI scope, so an unknown or deactivated tenant fails the job closed and every tenant-filtered `DbContext` in the scope is built under the right tenant. Only the Id travels: the record is never serialized into job storage.

There is no third, silent option:

| | enqueue | run |
|---|---|---|
| unmarked, tenant ambient | tenant Id stamped | runs under that tenant |
| unmarked, no tenant | **throws**, naming the job | — |
| unmarked, no tenant parameter | — | **job fails** |
| `[SystemJob]` | never stamped | runs tenant-less |
| tenant deleted or deactivated | — | **job fails** |

Mark the job class (or the method Hangfire invokes) `[SystemJob]` only when the work genuinely belongs to no tenant — a maintenance sweep, a fan-out, provisioning a tenant that is not usable yet. Today: `PurgeOrphanedFilesJob`, `PurgeDeletedFilesJob`, `AuditRetentionJob`, `EventingRetentionJob`, `TenantExpiryScanJob`, `TenantProvisioningJob`.

## Touching tenant data from a system job — `ITenantScope`

`ITenantScope` (`BuildingBlocks/Shared/Multitenancy/`) is the only way to enter a tenant outside a request. It loads the full `AppTenantInfo` from the store, installs it, and *then* creates the DI scope — that order is the whole point, because a `MultiTenantDbContext` captures its `TenantInfo`, and the tenant query filter with it, at construction.

```csharp
await tenantScope.RunAsync(tenantId, async (services, ct) =>
{
    var db = services.GetRequiredService<FilesDbContext>();   // built under tenantId
    await db.SaveChangesAsync(ct);
}, ct);

await tenantScope.RunForEachTenantAsync(async (tenant, services, ct) => { … }, ct);   // fan-out
```

Never write `IMultiTenantContextSetter` yourself — an architecture test fails if any file outside `AmbientTenantContext` names it.

`RunAsync` is what you want. `Begin` exists for Hangfire's activator alone and is **synchronous on purpose**: the ambient tenant is an `AsyncLocal`, and a write made in the continuation of an `async` method is discarded when that method returns, so an async `Begin` would hand back a scope whose tenant is already gone.

## Recurring jobs — `IRecurringJobManager`

`IJobService` has **no** recurring API. Register recurring jobs in the module's `MapEndpoints` with `IRecurringJobManager.AddOrUpdate<T>(...)`, always `TimeZoneInfo.Utc`:

```csharp
recurringJobs.AddOrUpdate<PurgeOrphanedFilesJob>("files:purge-orphaned",
    j => j.RunAsync(CancellationToken.None), Cron.Hourly(), new() { TimeZone = TimeZoneInfo.Utc });
```

Examples in the tree: `PurgeOrphanedFiles`/`PurgeDeletedFiles` (Files), `AuditRetentionJob` (Auditing), `TenantExpiryScanJob` (Multitenancy). Eventing is not a module and has no `MapEndpoints`, so `EventingRetentionScheduler` makes the same `AddOrUpdate` call from a hosted service at start-up.

## Tracing

`HangfireTelemetryFilter` stores the enqueuer's W3C `traceparent`/`tracestate` as job parameters and starts the job's span (source `Boilerplate.Hangfire`) as its child, so a request and the job it enqueued are one trace. A job enqueued with no trace in scope — a recurring trigger — starts a root span.

## The Job monitor & config (ADR-0009)

The **Job monitor** is Hangfire's dashboard — call it that, not "the dashboard" (that is the tenant client). `/jobs` (`HangfireOptions.Route`), mapped as a routed endpoint after `UseAuthentication`/`UseAuthorization` and gated by `.RequirePermission(SystemPermissions.Hangfire.View)` — a root-only operator permission. There is no dashboard credential: anonymous → 401, signed in without the permission → 403. Hangfire's own `DashboardOptions.Authorization` is empty on purpose, so ASP.NET Core authorization is the single gate.

- **Read-only unless `Hangfire.Manage`.** A Hangfire *async* authorization filter (always answers yes) checks `Manage` once per request through `IPermissionChecker` and stashes it for `IsReadOnlyFunc`, which is synchronous. Unknown means read-only. A read-only caller's write gets Hangfire's 401.
- **Antiforgery is on.** `AddHeroJobs` registers `IAntiforgery`, which is what switches on Hangfire's check of every write; its token cookie is `job_monitor_antiforgery`, `Path` = the route. A write without the page's `csrf-token` is 403 for everyone. No other endpoint binds forms, so nothing else is affected — if you add a form-bound endpoint, it will now require antiforgery too.
- **Two ways in.** API clients send `Authorization: Bearer`. A browser gets the `__Secure-job_monitor` cookie from `POST /api/v1/identity/operator/job-monitor-access` (root, `Hangfire.View`, refused while acting, audited), which the console calls before opening `/jobs` in a new tab. The cookie scheme (`JobMonitor.CookieScheme`) is picked by the forwarding default scheme only on the endpoint carrying `JobMonitorEndpointMetadata`, with no bearer header present; its `sid` is checked per request like a bearer token's. The cookie `Path` and the mapped route both come from `JobMonitor.RouteFrom(configuration)` — keep it that way, or the browser never sends the cookie.
- **`--frontend false`** scaffolds have no console, so their Job monitor is bearer-only.

## Gotchas

- A recurring job is triggered by the scheduler with no tenant in scope, so **every recurring job must be `[SystemJob]`** — otherwise it throws at trigger time. Per-tenant recurring work is a `[SystemJob]` that fans out with `ITenantScope.RunForEachTenantAsync`.
- A fan-out sweep catches **inside** the callback and logs the tenant id: `RunForEachTenantAsync` is fail-fast, so an uncaught failure on one tenant would leave the rest of the catalog unswept. The two Files purges and the audit retention sweep are written that way.
- Inside a fan-out the tenant filter is already doing the scoping, so don't reach for `IgnoreQueryFilters()` — lift only the *named* filter you actually need (`IgnoreQueryFilters([QueryFilters.SoftDelete])` in `PurgeDeletedFilesJob`, because those rows are soft-deleted by definition). A blanket `IgnoreQueryFilters()` would drop the tenant filter too and make the sweep cross-tenant again.
- The DbMigrator registers `NoOpJobService` whose methods **throw** — surfaces any accidental enqueue during migration. Don't enqueue from migration/seed paths.
- A job class is a normal DI-resolved type (scope-per-job via `AppJobActivator`); inject what you need.
