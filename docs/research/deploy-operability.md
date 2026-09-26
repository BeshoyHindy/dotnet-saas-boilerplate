# Deploy and operability defaults of the Dokploy stack

Research for [Review the deploy and operability defaults of the Dokploy stack](https://github.com/BeshoyHindy/dotnet-saas-boilerplate/issues/131),
part of [#87](https://github.com/BeshoyHindy/dotnet-saas-boilerplate/issues/87).

**Question**: what would go wrong operating a first product from the Dokploy stack as shipped —
backups and restore, migrations on deploy, resource limits and restart policies, graceful shutdown,
secrets and env handling, log retention and disk growth, and the zero-to-running path.

**Method**: the stack itself is the primary source — `deploy/dokploy/*.compose.yml`,
`deploy/dokploy/.env.example`, `docs/deploy-dokploy.md`, `docs/adr/0005-dokploy-deployment-shape.md`,
and the background-processing code the app runs in production (`src/BuildingBlocks/Jobs`,
`src/BuildingBlocks/Eventing/Outbox`, `src/Host/Boilerplate.DbMigrator`). Docker/PostgreSQL/ASP.NET
Core/Hangfire docs are cited only to back specific default-behaviour claims.

**Skipped per the ticket** (already a recorded known limit or a map decision): Aspire containers, the
first-compose cost, the `libgssapi` log, the per-process idempotency lock, dependency versions, the
object store (MinIO/RustFS choice). Fixes are for a decision ticket that graduates from this one —
this file reports findings only.

## Findings

| # | Area | Location | Severity | Fix size |
|---|---|---|---|---|
| D1 | Backups | `deploy/dokploy/data-services.compose.yml:20-44` | High | M |
| D2 | Resource limits | `deploy/dokploy/app.compose.yml`, `data-services.compose.yml` (all services) | Medium | S |
| D3 | Graceful shutdown | `deploy/dokploy/app.compose.yml:49-141`; `src/BuildingBlocks/Jobs/Extensions.cs:26-31`; `src/BuildingBlocks/Eventing/Outbox/OutboxDispatcherHostedService.cs:31-64` | Medium | S |
| D4 | Log retention (container stdout) | `deploy/dokploy/app.compose.yml`, `data-services.compose.yml` | Medium | S |
| D5 | Log retention (database rows) | `src/BuildingBlocks/Eventing/Outbox/EfCoreOutboxStore.cs:119-124`; `.../Inbox/EfCoreInboxStore.cs:24-52` | Medium | S |
| D6 | Zero-to-running docs | `docs/deploy-dokploy.md:29-35,300` | Low | S |

Counts: **6 findings** — 1 high · 4 medium · 1 low.

### D1 — No backup service and no restore path for the PostgreSQL volume

- **Location**: `deploy/dokploy/data-services.compose.yml:20-44` (the `postgres` service and its
  `pg_data` named volume); `docs/deploy-dokploy.md` (no backup/restore section anywhere in the file).
- **Severity**: high
- **Fix size**: M

Neither compose stack runs a backup job, and nothing in the deploy guide describes taking or
restoring one. `pg_data` is a plain named Docker volume with no snapshot, replication or export step —
every tenant's data (Identity, Multitenancy, Files metadata, the outbox/inbox) lives only on that one
volume on that one host. The guide documents a **rollback** path for the application image (§7, "set
`IMAGE_TAG` back and deploy") and is explicit that "a migration that has already run is not rolled
back by this, so a rollback across a destructive migration needs a restore, not a redeploy" — but no
restore procedure exists to perform, and it has never been exercised. A host disk failure, an
operator error (`docker volume rm`), or a destructive migration currently has no recovery path other
than "there is no backup."

### D2 — No per-service resource limits on either compose stack

- **Location**: `deploy/dokploy/app.compose.yml` (all services) and
  `deploy/dokploy/data-services.compose.yml` (all services) — no `mem_limit`, `mem_reservation` or
  `cpus` key anywhere in either file; `docs/deploy-dokploy.md:24` sets the minimum host at "4 GB RAM or
  more, nothing else on ports 80/443."
- **Severity**: medium
- **Fix size**: S

Neither the `deploy.resources.limits` syntax nor the legacy top-level `mem_limit`/`cpus` keys are set
on any service, in either compose file. On the 4 GB VPS the guide itself recommends,
Postgres, Valkey (with `--appendonly yes`), MinIO, the API (5 Hangfire workers plus the request
pipeline) and both Traefik/Dokploy's own processes share one memory pool with no per-container cap.
A memory leak or a burst in any one service can exhaust the host, taking down Traefik and Dokploy's
own UI alongside the application — there is no isolation between "one service misbehaves" and "the
whole box goes down."

### D3 — The API's shutdown grace period is shorter than what its own background work needs

- **Location**: `deploy/dokploy/app.compose.yml:49-141` (the `api` service — no `stop_grace_period`
  set); `src/BuildingBlocks/Jobs/Extensions.cs:26-31` (`AddHangfireServer`, 5 workers, no
  `ShutdownTimeout` override); `src/BuildingBlocks/Eventing/Outbox/OutboxDispatcherHostedService.cs:31-64`
  (a `BackgroundService` polling loop with no shutdown-timeout override either).
- **Severity**: medium
- **Fix size**: S

Docker Compose's default `stop_grace_period` is 10 seconds: it sends `SIGTERM` to the container's
main process and, if the process hasn't exited by then, sends `SIGKILL` ([Docker Compose file
reference, `stop_grace_period`](https://docs.docker.com/reference/compose-file/services/#stop_grace_period);
confirmed default behaviour across current Compose documentation and tooling). Neither compose file
sets this, so it applies to `api`, `postgres`, `valkey` and `minio` alike. Meanwhile ASP.NET Core's
Generic Host gives `StopAsync` — which every `IHostedService`, including `OutboxDispatcherHostedService`
and Hangfire's `BackgroundJobServer`, gets to run inside — a **30-second** default budget
(`HostOptions.ShutdownTimeout`, [`TimeSpan.FromSeconds(30)` in `dotnet/runtime`'s
`HostOptions.cs`](https://github.com/dotnet/runtime/blob/main/src/libraries/Microsoft.Extensions.Hosting/src/HostOptions.cs)).
Because `pull_policy: always` and `restart: unless-stopped` mean every redeploy recreates the `api`
container, and recreation is exactly a `SIGTERM`-then-wait-then-`SIGKILL` cycle, the 10-second Docker
clock can `SIGKILL` the process while it still believes it has up to 30 seconds to drain in-flight
HTTP requests, let a running Hangfire job finish, or let the outbox dispatcher finish a dispatch cycle
it had already started. This does not corrupt data — the outbox/inbox and Hangfire's own job state are
durable in Postgres, so an interrupted job is retried, not lost — but it turns every ordinary redeploy
into a small, avoidable window of dropped in-flight requests and abruptly-killed background work.

### D4 — No log rotation configured for any container, on either stack

- **Location**: `deploy/dokploy/app.compose.yml` and `deploy/dokploy/data-services.compose.yml` — no
  `logging:` key in either file.
- **Severity**: medium
- **Fix size**: S

Neither compose file sets a `logging` driver or options, so every container — `api`, the two clients,
`postgres`, `valkey`, `minio` — runs under the Docker Engine's default `json-file` driver with its
default `max-size`, which the Docker docs state is `-1` (unlimited): "The maximum size of the log
before it is rolled … Defaults to -1 (unlimited)" ([JSON File logging driver,
docs.docker.com](https://docs.docker.com/engine/logging/drivers/json-file/)). A long-lived production
deployment therefore accumulates unbounded stdout/stderr on disk for as long as the host runs, with
nothing in this stack capping it or rotating it out — the same disk that holds `pg_data` and
`minio_data`. There is no companion alert either (uptime monitoring is explicitly out of scope per the
ticket), so the first sign of the problem on an unattended first product would be the disk filling up.

### D5 — Outbox and inbox rows are never purged, unlike the Auditing module's own precedent

- **Location**: `src/BuildingBlocks/Eventing/Outbox/EfCoreOutboxStore.cs:119-124` (`MarkAsProcessedAsync`
  only stamps `ProcessedOnUtc`, never deletes the row) and
  `src/BuildingBlocks/Eventing/Inbox/EfCoreInboxStore.cs:24-52` (`MarkProcessedAsync` inserts one
  `InboxMessage` row per `(eventId, handlerName)` forever). Contrast with
  `src/Modules/Auditing/Modules.Auditing/Persistence/AuditRetentionJob.cs`, a daily
  `[SystemJob]` that prunes the audit table per tenant with a bounded-batch `ExecuteDeleteAsync` loop.
- **Severity**: medium
- **Fix size**: S

The ticket's "log retention and disk growth" question is not only about container stdout (D4) — it
applies inside the one PostgreSQL database too. Every dispatched outbox message and every inbox
dedup record is a permanent row: `OutboxDispatcher.DispatchAsync` (`OutboxDispatcher.cs:73-74`) marks a
message processed rather than removing it, and the inbox has no equivalent of a processed cut-off
either. On a first product with any sustained integration-event volume, both tables grow without
bound on the same disk as everything else, with no sweep to point at — the template already has the
pattern to copy (`AuditRetentionJob` plus `AuditRetentionOptions`), it just was not extended to
Eventing's own tables.

### D6 — The zero-to-running guide's DNS and first-sign-in steps don't match the two-client model

- **Location**: `docs/deploy-dokploy.md:29-35` (the "Three hostnames" DNS table) and
  `docs/deploy-dokploy.md:300` (first-sign-in instruction).
- **Severity**: low
- **Fix size**: S

§0 tells a fresh-server operator to create **three** DNS records — API, "Console" (example
`app.example.com`, described as "the web front end"), and Storage — but §3's variable worksheet a few
sections later has **four** domain variables (`API_DOMAIN`, `DASHBOARD_DOMAIN`, `CONSOLE_DOMAIN`,
`STORAGE_DOMAIN`), and `app.compose.yml` routes `dashboard` and `console` as two separate Traefik
routers on two separate hostnames. A reader following §0 literally creates only three `A` records and
has no `CONSOLE_DOMAIN` record to point at — the same class of DNS-not-resolved-yet failure the
guide's own §10 table warns about ("404 from Traefik on a domain … does not match the record"), except
self-inflicted by the walkthrough. Separately, §5 tells the reader to sign in as the seeded root
tenant admin (`admin@root.com`) at `https://app.example.com` — the **Dashboard** domain — but
`CONTEXT.md`'s own glossary is explicit that root-tenant operators sign in at the **Console**, and that
"a tenant user who signs in [to the console] is told it is not their app" (i.e. the two are not
interchangeable). The first-run instruction points a fresh operator at the wrong client's domain.

## Checked and sound

- **Migration-on-deploy and failure handling.** The one-shot `migrator` (`app.compose.yml:31-44`)
  takes a Postgres advisory lock (`src/Host/Boilerplate.DbMigrator/PostgresMigratorLock.cs`) so
  concurrent deploys serialise rather than race, waits out a cold-starting Postgres with bounded
  backoff, and exits non-zero on any failure (`MigratorCommand.cs`); `api`'s
  `depends_on: migrator: condition: service_completed_successfully` means a failed migration fails the
  whole deploy and the API never starts against a half-migrated schema. A failed migration is also
  safe for the schema itself, not only for the new `api`: this repo pins
  `Microsoft.EntityFrameworkCore` `10.0.8` (`src/Directory.Packages.props:53`), and since EF Core 9.0
  `MigrateAsync` applies **all** pending migrations for a context inside one transaction as part of
  its migration lock, rather than one transaction per migration — a failure partway through a batch
  rolls the whole batch back, it does not leave the schema half-advanced. And because Dokploy's
  compose deploy is a plain `docker compose … up -d` (no `down` first — [Dokploy's own Docker Compose
  reference](https://www.mintlify.com/Dokploy/dokploy/api-reference/docker/compose) and community
  write-ups of the generated command agree it is `up -d --build --remove-orphans`, never a stop-first
  redeploy), Compose only recreates a service whose config changed — so the **previous** `api`
  container keeps serving on the untouched old schema for the whole time the migrator is running,
  and stays serving if the migrator fails. The one caveat — a rollback does not undo an
  already-applied migration — is already stated plainly in `docs/deploy-dokploy.md` §7 rather than
  being an undocumented surprise.
- **The seeded root-admin password does not get reset on every deploy.**
  `IdentityDbInitializer.SeedAdminUserAsync` (`src/Modules/Identity/Modules.Identity/Data/IdentityDbInitializer.cs:179-218`)
  only sets `PasswordHash` on the create-user branch (`is not AppUser adminUser`); an operator who
  already changed the password past `SEED_ADMIN_PASSWORD` is untouched by the next redeploy, even
  though the same env value stays configured in the stack for its whole lifetime.
- **Restart policies.** `unless-stopped` on every long-running service and `restart: "no"` on every
  one-shot (`migrator`, `minio-volume-owner`, `minio-init`, `minio-public-prefix`) is the correct
  pairing — a one-shot that got restarted forever would re-run `chown`/`mb`/`anonymous set` in a
  crash loop instead of exiting cleanly.
- **Secrets and env handling.** Values are Dokploy Environment-tab variables, documented by key name
  only in `.env.example` with no defaults (so a forgotten value fails the boot loudly instead of
  substituting an empty string), and `ProductionConfigurationGuard` refuses to boot on placeholder-
  looking values. ADR-0005 records that a file-mounted secret bundle was considered and rejected as
  infrastructure the template should not presume; external secret managers are explicitly out of
  scope for this ticket, so this is treated as an accepted, recorded trade rather than a finding.
- **The zero-to-running path, apart from D6.** `docs/deploy-dokploy.md` walks a blank VPS through
  installing Dokploy, secrets generation, `PROXY_KNOWN_NETWORK` derivation, deploying the data stack
  before the app stack, and verification curls against `/health/live` and `/health/ready` — with a
  troubleshooting table covering the failure modes the compose contract tests also encode. The DNS
  count and first-sign-in domain are the one gap found (D6); the rest of the walkthrough matches the
  stack as shipped.
- **Redeploy is stop-old-then-start-new, not rolling — undocumented but low-stakes for a first
  product.** Each application service runs one replica, so `docker compose up -d` recreating `api` (or
  `dashboard`/`console`) is a brief hard cutover, not a zero-downtime rollout; nothing in
  `docs/deploy-dokploy.md` states this. Worth one line in the guide, but not worth a separate finding
  here — a first product on one VPS has no second replica for Traefik to drain into either way, so
  there is no cheaper fix available than adding replicas, which is out of shape for this stack.
- **Readiness and traffic cutover.** The chiselled API image can carry no container `HEALTHCHECK`, so
  gating on Traefik's load-balancer healthcheck against `/health/ready` (with `passhostheader=true` so
  `AllowedHosts` filtering doesn't 400 the probe) is a sound substitute, and it is covered by
  `deploy/dokploy/tests/compose-contract.test.sh`.
- **Data/app stack separation.** Two separate compose projects joined only via the shared external
  `dokploy-network` mean an application redeploy structurally cannot recreate, restart or wipe the
  database volume — verified in both `data-services.compose.yml`'s comments and the compose contract
  tests.
