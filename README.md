# Boilerplate

A production-ready starter for multi-tenant SaaS: a modular .NET 10 monolith (vertical slices,
CQRS via a source-generated mediator, EF Core 10 on PostgreSQL) plus one React 19 console, wired
for local orchestration with .NET Aspire.

`Boilerplate` is the placeholder root name. A new product renames it in one command
(`dotnet new saas -n Acme`) — see [`docs/adr/0001-placeholder-namespace-and-dotnet-new-rename.md`](docs/adr/0001-placeholder-namespace-and-dotnet-new-rename.md).

Three docs carry the rest:

- [`CONTEXT.md`](CONTEXT.md) — the glossary: the words this project uses, and the ones it refuses.
- [`docs/new-project-guide.md`](docs/new-project-guide.md) — scaffold, first run, the tenancy rules
  for adding code, CI and the GitHub settings an owner must configure, known limits.
- [`docs/deploy-dokploy.md`](docs/deploy-dokploy.md) — blank server to a healthy HTTPS deployment.

## Start a new product from it

```bash
git clone https://github.com/BeshoyHindy/dotnet-saas-boilerplate Acme && cd Acme
dotnet new install .            # the repository root IS the template
dotnet new saas -n Acme -o ../Acme.App
```

Nothing is published to NuGet.org: `dotnet new install <path>` on a clone is the supported
install route, so there is no package version to keep in step with the source.

| Option | Default | Drops when `false` |
|---|---|---|
| `--frontend` | `true` | `clients/**`, the console container in `docker-compose.yml` and `deploy/dokploy/app.compose.yml`, the Aspire client resources, `frontend.yml` |
| `--aspire` | `true` | `src/Host/Boilerplate.AppHost` and its solution folder |
| `--sandcastle` | `true` | `.sandcastle/`, `sandcastle.config.mts`, the root pnpm project that exists only for them, `sandcastle.yml` |

`--skipRestore`, `--contactEmail`, `--contactUrl` and `--mailFrom` are also accepted
(`dotnet new saas --help` lists them all).

`Boilerplate` → `Acme` is a plain text replacement across every file type, and a second derived
symbol renames the lowercase/kebab form (`boilerplate` → `acme`: image names, database and bucket
names, the compose project, npm scopes, JWT issuer and audience, `localStorage` key prefixes).
The two `UserSecretsId` GUIDs are regenerated per scaffold, and the API and DbMigrator keep
sharing one. A scaffold carries `AGENTS.md`, `CLAUDE.md`, `.agents/rules/`, `CONTRIBUTING.md`
and `SECURITY.md` — a new project has to be workable by the same agent pipeline on day one — plus
`CONTEXT.md`, `docs/agents/` and `docs/new-project-guide.md`, which are the vocabulary and the
conventions that pipeline reads.
What it deliberately does **not** carry: `LICENSE` (pick your own), `README.md` (replaced by
[`README-template.md`](README-template.md)), the vendored `.agents/skills/` and
`.agents/workflows/` with `skills-lock.json`, the brand gate and template smoke (this repository's
own gates), and ADR-0001 itself.

Run the whole thing locally — scaffold, build, test, brand-grep — with
[`scripts/template-smoke.sh`](scripts/template-smoke.sh).

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
<https://localhost:7030/scalar>; the console at <http://localhost:5173>; the Mailpit inbox at
<http://localhost:8025>. Aspire starts PostgreSQL, Valkey, MinIO and Mailpit, runs the migrator to
completion, then the API, then the console.

**Only one AppHost instance at a time.** MinIO binds fixed host ports 9000 and 9001, and its
container is persistent — so a second checkout or worktree that has run the AppHost leaves
containers holding those ports. The new `minio` container then comes up attached to no network,
`minio-init` loops printing `waiting for minio...`, and since the API waits for that init to
complete, neither the API nor the console ever starts. `docker ps` shows two `minio-*` containers;
`docker logs <minio-init>` shows the loop. Remove the other instance's persistent containers
(`minio`, `minio-init`, `postgres`, `redis`) and run again.

Separately: the initial migrations were regenerated while this template was built, so a database
migrated before that is incompatible and needs a fresh volume —
`docker volume rm boilerplate-postgres-data` (destructive; local development data).

The MinIO password, the seeded root admin password and the demo password are Aspire parameters,
generated on first run and persisted to the AppHost's user-secrets; read the current values from the
Aspire dashboard (Resources → Parameters).

### Demo accounts

The AppHost runs the migrator as `apply --seed --demo`, so a fresh stack comes up with two demo
tenants to sign in to besides `root`:

| Tenant | Accounts |
|---|---|
| `acme` (Acme Corp) | `admin@acme.com` (Admin), `manager@acme.com` (Manager), `support@acme.com` (Support), `alice@acme.com`, `bob@acme.com` (Basic) |
| `globex` (Globex) | `admin@globex.com` (Admin), `dave@globex.com` (Basic) |

All of them share one password: the `seed-demo-password` parameter in the Aspire dashboard. The root
operator `admin@root.com` is **not** part of the demo set — it keeps `seed-admin-password`.

Demo seeding is opt-in and refused outright in Production. Drop `--demo` from the migrator's args in
`AppHost.cs` to stop seeding it; see [`docs/new-project-guide.md`](docs/new-project-guide.md) for
what to do with the seeder when you start a real product.

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
why the secrets have no default in `docker-compose.yml` and `scripts/local-env.sh` generates them
instead — including `SEED_DEMO_PASSWORD`, the one password the demo accounts above share. The
`migrator` service is the single exception to Production here: it runs as Development, because demo
seeding is refused in a Production host. Every other setting has a local default — see
[`.env.example`](.env.example) for the full list. Real deployments configure these images through
Dokploy environment variables (ADR-0005)
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
| `clients/console` | The React console; `clients/openapi/v1.json` is the contract it generates from |
| `docker-compose.yml` | Runs the production images locally, with `.env.example` |
| `deploy/dokploy/` | Dokploy compose stacks, env contract and deploy script |
| `docs/adr/` | Architecture decision records |
| `.template.config/` | The `dotnet new saas` definition — this repo *is* the template |

## Contributing

See [`CONTRIBUTING.md`](CONTRIBUTING.md). Branch from and target `develop`; `main` only receives
release and hotfix merges (ADR-0007).

## License

MIT — see [`LICENSE`](LICENSE).
