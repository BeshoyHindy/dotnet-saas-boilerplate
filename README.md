# Boilerplate

A production-ready starter for multi-tenant SaaS: a modular .NET 10 monolith (vertical slices,
CQRS via a source-generated mediator, EF Core 10 on PostgreSQL) plus two React 19 clients, wired
for local orchestration with .NET Aspire.

`Boilerplate` is the placeholder root name. A new product renames it in one command
(`dotnet new saas -n Acme`) — see [`docs/adr/0001-placeholder-namespace-and-dotnet-new-rename.md`](docs/adr/0001-placeholder-namespace-and-dotnet-new-rename.md).

## What's in the box

- **Modules** (bounded contexts, each with a `.Contracts` project as its only public surface):
  Identity, Multitenancy, Files, Auditing, Notifications.
- **BuildingBlocks**: core domain primitives, persistence, web pipeline, caching (HybridCache on
  Valkey), eventing (outbox/inbox), jobs (Hangfire), storage (S3/MinIO), mailing.
- **Hosts**: `Boilerplate.Api` (composition root), `Boilerplate.DbMigrator` (one-shot migrate/seed —
  the API never migrates at startup), `Boilerplate.AppHost` (Aspire orchestrator).
- **Client**: `clients/console` — one React 19 app for tenant users and root operators (ADR-0004), +
  Vite + TypeScript, TanStack Query, React Router, Radix + Tailwind.
- **Deploy**: one multi-target image definition (`src/Host/Dockerfile`, targets `api` and
  `migrator`), a root `docker-compose.yml` that runs those images locally, and Dokploy
  (`deploy/dokploy`).

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download) (pinned in `global.json`)
- [Docker](https://www.docker.com/) — Postgres, Valkey and MinIO are started by Aspire, and the
  integration tests use Testcontainers
- [Node 20+](https://nodejs.org/) for the React clients

## Run

```bash
bash scripts/dev-secrets.sh                          # once per clone — see below
dotnet run --project src/Host/Boilerplate.AppHost
```

The repository ships **no credentials**. `scripts/dev-secrets.sh` generates a JWT signing key into the
local [user-secrets](https://learn.microsoft.com/aspnet/core/security/app-secrets) store of the API
(shared with the migrator); the API refuses to start without one. Anything else a host needs —
connection strings, S3 and SMTP credentials, the seeded admin password — comes from Aspire
parameters, environment variables or that same store, never from `appsettings*.json`.

The Aspire dashboard is at <https://localhost:15888>; the API and its Scalar reference at
<https://localhost:7030/scalar>; admin at <http://localhost:5173>; dashboard at
<http://localhost:5174>; the Mailpit inbox at <http://localhost:8025>. Aspire starts PostgreSQL,
Valkey, MinIO and Mailpit, runs the migrator to completion, then the API, then the clients.

The MinIO password and the seeded root admin password are Aspire parameters, generated on first run
and persisted to the AppHost's user-secrets; read the current values from the Aspire dashboard. The
migrator seeds only the root tenant and its admin user — there is no demo data.

## Run the container images locally

```bash
bash scripts/local-env.sh                  # once — writes .env with generated secrets
docker compose up --build                  # API :8080, console :8081, Mailpit inbox :8025
curl -fsS http://localhost:8080/health/ready
docker compose down -v
```

This builds and runs the same `api`, `migrator` and console images a deployment uses, against
PostgreSQL, Valkey, MinIO and a Mailpit mail catcher. The containers run as **Production**, so the
same fail-fast guards apply as on a server: no placeholder secrets, no `AllowedHosts: *`. That is
why the four secrets have no default in `docker-compose.yml` and `scripts/local-env.sh` generates
them instead. Every other setting has a local default — see [`.env.example`](.env.example) for the
full list. Real deployments configure these images through Dokploy environment variables (ADR-0005)
— see [`docs/deploy-dokploy.md`](docs/deploy-dokploy.md), which takes a blank server to a healthy
HTTPS deployment.

| Symptom | Likely cause |
|---|---|
| `POSTGRES_PASSWORD ... run scripts/local-env.sh` at `docker compose up` | No `.env` yet. Run the script; the error names the missing variable. |
| `Production configuration is not usable: Missing required configuration 'AllowedHosts'` | The API refuses to answer for any Host header in Production. List the hostnames it serves, semicolon-separated; `*` is rejected. |
| `Production configuration is not usable: … still holds a template placeholder` | A secret carries a sample value such as `changeme` or `dev-only`. Regenerate with `bash scripts/local-env.sh --force`. |
| `ProxyOptions: Enabled is true but nothing is trusted` | Production enables forwarded headers for a Traefik deployment and deliberately trusts no one by default. This compose file publishes the API directly, so it sets `ProxyOptions__Enabled=false`; behind a real proxy, name the proxy's network instead. |
| Console shows a CORS error | `APP_CONSOLE_URL` does not match the origin the browser actually uses; it is the API's CORS allow-list entry. |
| `migrator` retries Postgres for 2 minutes then fails | Usually a `POSTGRES_PASSWORD` change against an existing `pg_data` volume. `docker compose down -v` (destructive) and start over. |

## Test

```bash
dotnet test src/Boilerplate.slnx          # unit + architecture + Testcontainers integration
cd clients/console && pnpm test           # Vitest units
cd clients/console && pnpm test:e2e       # Playwright smoke suite
```

## Repository layout

| Path | What |
|---|---|
| `src/BuildingBlocks/` | Shared framework libraries |
| `src/Modules/{Name}/` | Bounded contexts (runtime project + `.Contracts`) |
| `src/Host/` | API, AppHost, DbMigrator, Migrations |
| `src/Tests/` | Unit, architecture (NetArchTest) and integration (Testcontainers) tests |
| `clients/` | The two React apps |
| `docker-compose.yml` | Runs the production images locally, with `.env.example` |
| `deploy/dokploy/` | Dokploy compose stacks, env contract and deploy script |
| `docs/adr/` | Architecture decision records |

## Contributing

See [`CONTRIBUTING.md`](CONTRIBUTING.md). Branch from and target `develop`; `main` only receives
release and hotfix merges (ADR-0007).

## License

MIT — see [`LICENSE`](LICENSE).
