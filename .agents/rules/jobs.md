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

- Jobs run on the server with **no HTTP/tenant context** — restore Finbuckle tenant context inside the job (fresh scope + `IMultiTenantContextSetter`) before touching a tenant-filtered DbContext.
- The DbMigrator registers `NoOpJobService` whose methods **throw** — surfaces any accidental enqueue during migration. Don't enqueue from migration/seed paths.
- A job class is a normal DI-resolved type (scope-per-job via `AppJobActivator`); inject what you need.
