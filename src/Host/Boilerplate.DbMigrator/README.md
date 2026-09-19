# Boilerplate DbMigrator

One-shot console application that applies EF Core migrations across the
tenant catalog and every tenant's per-module databases, then exits.

## Why a separate project

`Database.MigrateAsync()` at app startup is convenient but has well-known
production issues:

- The runtime app needs DDL permissions on every database it migrates.
- Multiple replicas race on startup — only one wins, the rest can fail
  or wait forever.
- Slow startup when migrations are pending.
- Mixed deployment + runtime concerns: a deployment-time action runs as
  part of the request path.

Industry practice in mid-to-large .NET shops is to run migrations as an
**explicit deployment step**, with elevated DB credentials, before the
runtime app starts. This project is that step.

## Usage

```bash
# Default — apply pending migrations for the tenant catalog + every tenant.
dotnet run --project src/Host/Boilerplate.DbMigrator -- apply

# Apply only to one tenant.
dotnet run --project src/Host/Boilerplate.DbMigrator -- apply --tenant root

# Apply only the tenant catalog (no per-tenant pass).
dotnet run --project src/Host/Boilerplate.DbMigrator -- apply --catalog-only

# Preview what would run without touching the database.
dotnet run --project src/Host/Boilerplate.DbMigrator -- list-pending

# Apply migrations AND run idempotent seed data.
dotnet run --project src/Host/Boilerplate.DbMigrator -- apply --seed

# Just the seed step (assumes schema is already current).
dotnet run --project src/Host/Boilerplate.DbMigrator -- seed

# Apply, seed, and add the demo accounts (development only — see below).
dotnet run --project src/Host/Boilerplate.DbMigrator -- apply --seed --demo
```

Exit codes: `0` on success, `1` on any failure (see logged exception).

## Configuration

Reads from the same `appsettings.json` / `appsettings.{Environment}.json`
as `Boilerplate.Api` (both files are linked into the project so they
stay in lock-step). Override anything via environment variables:

| Variable                                  | Notes                                       |
| ----------------------------------------- | ------------------------------------------- |
| `DatabaseOptions__Provider`               | `POSTGRESQL` (only provider currently)      |
| `DatabaseOptions__ConnectionString`       | Use elevated DDL credentials here           |
| `DatabaseOptions__MigrationsAssembly`     | `Boilerplate.Migrations.PostgreSQL`         |
| `CachingOptions__Redis`                   | Optional — only used by module DI graphs    |
| `Logging__LogLevel__Default`              | `Information` is the default                |
| `Seed__DefaultAdminPassword`              | Password for every seeded tenant admin      |
| `Seed__DemoPassword`                      | Required by `--demo`; shared by demo accounts |

## Deployment patterns

### Kubernetes (Helm)

Use a `Job` (or `pre-install`/`pre-upgrade` Helm hook) that runs the
migrator container image, then deploy the API only after the Job
succeeds. The image is the `migrator` target of `src/Host/Dockerfile`
(built from the repository root, ADR-0005):

```bash
docker build -f src/Host/Dockerfile --target migrator \
  -t boilerplate-db-migrator:local .
```

```yaml
# helm/templates/migrator-job.yaml
apiVersion: batch/v1
kind: Job
metadata:
  name: {{ .Release.Name }}-db-migrator
  annotations:
    "helm.sh/hook": pre-install,pre-upgrade
    "helm.sh/hook-delete-policy": before-hook-creation,hook-succeeded
spec:
  backoffLimit: 0
  template:
    spec:
      restartPolicy: Never
      containers:
        - name: migrator
          image: ghcr.io/your-org/boilerplate-db-migrator:{{ .Chart.AppVersion }}
          args: ["apply"]
          env:
            - name: DatabaseOptions__ConnectionString
              valueFrom: { secretKeyRef: { name: db-ddl, key: connection } }
            - name: DatabaseOptions__Provider
              value: POSTGRESQL
            - name: DatabaseOptions__MigrationsAssembly
              value: Boilerplate.Migrations.PostgreSQL
```

### GitHub Actions / Azure Pipelines

Run as a step before the deploy step:

```yaml
- name: Migrate database
  run: |
    dotnet run --project src/Host/Boilerplate.DbMigrator -- apply
  env:
    DatabaseOptions__ConnectionString: ${{ secrets.DB_DDL_CONNECTION }}
    DatabaseOptions__Provider: POSTGRESQL
    DatabaseOptions__MigrationsAssembly: Boilerplate.Migrations.PostgreSQL
```

### Local development

There is **no** development-only auto-migration *or* auto-seed in the
API. In every environment, the migrator is the only path that touches
schema OR data. The two convenient ways to run it locally are:

- **Aspire**: `dotnet run --project src/Host/Boilerplate.AppHost` —
  Aspire already chains the migrator as a `WaitForCompletion`
  dependency of the API, so the API never starts against an
  unmigrated database.
- **Raw**: run the migrator once after pulling, before starting the
  API:

  ```bash
  # Schema only (every env)
  dotnet run --project src/Host/Boilerplate.DbMigrator -- apply

  # Then start the API
  dotnet run --project src/Host/Boilerplate.Api
  ```

Without `--demo` the migrator seeds the root tenant and its admin user
only, and fresh tenants created via `POST /api/v1/tenants` come up with
just a tenant admin user. This matches production behaviour.

## Demo accounts (`--demo`)

`--demo` adds the demo dataset in `DemoSeed/DemoDataset.cs`: the `acme`
and `globex` tenants, a handful of people in each, two custom roles
(`Manager`, `Support`) and a couple of groups. It exists so a fresh
stack has something to sign in as; a starter nobody can log into cannot
be evaluated.

- **Opt-in.** Nothing seeds demo data unless the flag is passed. The
  Aspire AppHost and the root `docker-compose.yml` pass it; the Dokploy
  stacks do not, and a contract test asserts they never will.
- **Never in Production.** `--demo` against a Production host fails with
  exit code 1 before touching the database. There is no override: every
  demo account shares one password, which is a back door on a server.
- **One password, from configuration.** `Seed:DemoPassword`
  (`Seed__DemoPassword`) — no literal anywhere in the repository. Aspire
  generates and persists it as the `seed-demo-password` parameter (read
  it in the dashboard under Parameters); `scripts/local-env.sh`
  generates `SEED_DEMO_PASSWORD` into `.env` for compose. It must
  satisfy the Identity password policy, or the run fails saying which
  rule it broke.
- **Idempotent.** Re-running changes nothing. It never rewrites a
  password you changed and never reactivates a user you deactivated; it
  does recreate a row that is gone (a hard-deleted user comes back), so
  "drop the database and run it again" works.
- **Tenants are ready when it returns.** Each demo tenant is created
  through `ITenantService` — the service the runtime's CreateTenant path
  uses — then migrated and seeded inline, and a completed provisioning
  record is written so the console's Provisioning panel shows a history.

Starting a real product: delete `DemoSeed/` and the `--demo` wiring, or
keep the seeder and replace `DemoDataset` with your own people.

## API behavior when schema is behind

If the API boots against a database whose schema is behind the running
build, the `db:tenants-migrations` health check returns `Unhealthy`
and `GET /health/ready` returns `503 Service Unavailable` with the
list of pending tenants + migration names in the response body.
`GET /health/live` continues to return `200 OK` because the process
itself is alive — so Kubernetes will not crash-loop the pod, but the
readiness probe will keep it out of rotation until DbMigrator runs.

This means: a failed (or skipped) migrator step surfaces as a clear
operator-visible health-check failure rather than as cryptic EF
errors per request.

## What it actually does

1. Builds the same DI container the API does (every module's
   `ConfigureServices`), with web-only concerns (CORS, OpenAPI, jobs,
   mailing, OpenTelemetry, idempotency) disabled.
2. Removes every `IHostedService` so background workers don't fight
   with the migrator.
3. Applies `TenantDbContext` migrations and seeds the root tenant if
   missing.
4. Reads every `AppTenantInfo` from the catalog and, for each, calls
   `ITenantService.MigrateTenantAsync` (and `SeedTenantAsync` if
   `--seed` is set) which walks every registered `IDbInitializer`
   inside a scoped multi-tenant context.
5. With `--demo`, runs `DemoSeed/DemoSeeder` last, once every existing
   tenant is at head.

The per-tenant pass reuses `TenantService.MigrateTenantAsync` — the
exact code path the runtime app uses today — so behavior is identical
between the migrator and the API's startup pass when both are enabled.
