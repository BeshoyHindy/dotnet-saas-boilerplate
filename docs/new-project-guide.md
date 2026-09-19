# New project guide

From an empty machine to a running product, and then to the rules that keep it multi-tenant.

Read [`CONTEXT.md`](../CONTEXT.md) first — one page, and it fixes the vocabulary the rest of this
guide uses.

## 1. Scaffold

Already inside a project that was scaffolded from the template? Skip to §2.

The template repository's root *is* the template. Nothing is published to NuGet.org, so a clone plus
a local install is the whole distribution:

```bash
git clone <this repo> Boilerplate && cd Boilerplate
dotnet new install .                 # installs from this working directory
dotnet new saas -n Acme -o ../Acme
```

`-n Acme` renames everything: `Boilerplate` → `Acme` in namespaces, project and file names, and a
derived lowercase form renames image names, database and bucket names, the compose project, npm
scopes, the JWT issuer and audience and the console's `localStorage` prefixes.

| Parameter | Default | What `false` drops |
|---|---|---|
| `--frontend` | `true` | `clients/**`, the console service in `docker-compose.yml` and the app stack, the console image, the client CI workflow, ADR-0004 |
| `--aspire` | `true` | the AppHost project |
| `--sandcastle` | `true` | `.sandcastle/`, `sandcastle.config.mts`, the root pnpm project that exists only for them, its workflow, ADR-0006 |

`dotnet new saas --help` lists the rest (`--skipRestore`, `--contactEmail`, `--contactUrl`,
`--mailFrom`).

A scaffold **carries** the whole agent-facing surface, because a new project has to be workable by
the same pipeline on day one: `AGENTS.md`, `CLAUDE.md`, `.agents/rules/`, `CONTEXT.md`,
`docs/agents/`, this guide, `CONTRIBUTING.md` and `SECURITY.md` — all renamed along with everything
else. `.github/`, `scripts/`, `docs/` and `global.json` ship too: the project needs its own CI, its
own scripts and its own runbook.

It deliberately does **not** carry: `LICENSE` (pick your own), the template repository's `README.md`
(`README-template.md` becomes yours), `GEMINI.md`, the vendored `.agents/skills/` and
`.agents/workflows/` with `skills-lock.json`, the brand gate and the template smoke — those two are
the template repository's own gates — and the placeholder-namespace ADR itself.

Uninstall with `dotnet new uninstall <path>` when you are done. To re-prove the whole path locally —
scaffold, build, test, brand-grep — run `scripts/template-smoke.sh` in the template repository.

## 2. First run

```bash
chmod +x scripts/*.sh deploy/dokploy/*.sh deploy/dokploy/tests/*.sh
```

`dotnet new` copies file content but not the POSIX executable bit. Everything here also works when
invoked as `bash <script>`, which is how the docs spell it.

**Add a LICENSE.** The scaffold ships without one; pick the licence your product needs.

**Generate the development secrets.** The repository ships no credentials at all, and the API refuses
to start without a JWT signing key:

```bash
bash scripts/dev-secrets.sh
```

It writes into the .NET user-secrets store (outside the repository), shared by the API and the
migrator. `--force` regenerates.

### Either: the whole stack under Aspire

```bash
dotnet run --project src/Host/Acme.AppHost
```

Aspire starts PostgreSQL, Valkey, MinIO and Mailpit, runs the migrator to completion, then the API,
then the console. The dashboard is at <https://localhost:15888>; it also shows the generated MinIO,
seeded-admin and demo passwords (Resources → Parameters).

#### Troubleshooting the AppHost

**Nothing starts, and `minio-init` prints `waiting for minio...` forever.**

Only one AppHost instance can run at a time. MinIO binds fixed host ports 9000 and 9001 and its
container is `Persistent`, so a second checkout or worktree that has ever run the AppHost leaves a
`minio` container holding those ports. The new run's `minio` container then comes up attached to no
network; `minio-init` cannot reach it and loops; and because the API is declared
`.WaitForCompletion(minioInit)`, the API — and the console behind it — never starts. The dashboard
just shows resources waiting.

How to see it: `docker ps` lists two `minio-*` containers, from two different projects.
`docker logs <minio-init container>` shows the repeated `waiting for minio...`.

Remedy: stop the other instance and remove its persistent containers (`minio`, `minio-init`, and
while you are there `postgres` and `redis`), then run again. Or simply don't run two AppHosts.

**A separate note on the database.** The initial migrations were regenerated during this template's
construction, so a database migrated before that is incompatible with the current code and needs a
fresh volume: `docker volume rm <app-prefix>-postgres-data` — `boilerplate-postgres-data` here,
`acme-postgres-data` in a project scaffolded as `Acme`. Destructive, and it is local development
data.

### Or: the container images

```bash
bash scripts/local-env.sh            # once — writes a gitignored .env with generated secrets
docker compose up
curl -fsS http://localhost:8080/health/ready
```

These are the same images a deployment runs, and they run as **Production**, so the production
fail-fast guards apply: no placeholder secret, no `AllowedHosts: *`. That is why the secrets have no
defaults and the script generates them. `docker compose down -v` resets the data.

The one exception is the `migrator` service, which runs as Development: it is asked to seed the demo
accounts below, and demo seeding is refused in a Production host. Remove `--demo` and that
`DOTNET_ENVIRONMENT` line together if you want the migrator on Production too.

### The console on its own

```bash
cd clients/console && pnpm install && pnpm dev     # http://localhost:5173
```

The dev server proxies `/api`, `/openapi`, `/scalar` and `/health` to the API (target from
`VITE_API_BASE_URL`, default `http://localhost:5030`). **Keep it that way.** The refresh token is an
`HttpOnly; SameSite=Strict` cookie and CORS allows no credentials, so a console served from a
different origin than the API can never refresh a session. The runtime `apiBase` is `""` — same
origin — in every environment, and the nginx image proxies exactly like the dev server does. A CORS
error in the console is a proxy misconfiguration, not a reason to point the client elsewhere.

Sign in as the seeded root admin (`admin@root.com`) with the password from the Aspire dashboard or
`.env`, and rotate it.

### Demo accounts

A starter with nothing to sign in to cannot be evaluated, so the migrator runs with `--demo` in both
local paths and seeds two tenants besides `root`:

| Tenant | Account | Role | Groups |
|---|---|---|---|
| `acme` (Acme Corp) | `admin@acme.com` | Admin | — |
| `acme` | `manager@acme.com` (Maya Lin) | Manager | All Users, Support Desk |
| `acme` | `support@acme.com` (Sam Rivera) | Support | All Users, Support Desk |
| `acme` | `alice@acme.com` (Alice Nguyen) | Basic | All Users, Engineering |
| `acme` | `bob@acme.com` (Bob Patel) | Basic | All Users, Engineering |
| `globex` (Globex) | `admin@globex.com` | Admin | Operations |
| `globex` | `dave@globex.com` (Dave Hartwell) | Basic | All Users, Operations |

`Manager` and `Support` are custom roles the seeder creates — neither Admin nor Basic — so the role
and permission screens have something real to show.

**The password.** All demo accounts share one, and it is never a literal in the repository:

- **Aspire**: the `seed-demo-password` parameter, generated on first run and persisted to the
  AppHost's user-secrets. Read it in the dashboard under Resources → Parameters.
- **Compose**: `SEED_DEMO_PASSWORD` in `.env`, generated by `bash scripts/local-env.sh`.

The root operator `admin@root.com` is deliberately *not* a demo account — it keeps the separate
admin password, so handing someone the demo set does not hand over the platform.

**Turning it off.** Demo seeding is opt-in and is refused outright when the environment is
Production (exit code 1, before any database write). To stop seeding it locally, drop `--demo` from
the migrator's args in `AppHost.cs` and from the `migrator` command in `docker-compose.yml`.

**When you start a real product**, delete `src/Host/Acme.DbMigrator/DemoSeed/` along with the
`--demo` flag and the `Seed__DemoPassword` wiring — or keep the seeder and replace `DemoDataset`
with your own tenants and people. Nothing else depends on it: the framework's own seed (root tenant,
default roles, system groups, tenant admin) is a separate step that runs with or without `--demo`.

## 3. Adding to it without breaking tenancy

The one invariant (ADR-0002): **a caller never names a tenant.** The tenant comes from the signed
`tenant` claim on an authenticated request, and from the `{tenant}` route segment on the handful of
anonymous auth endpoints — nowhere else. Every rule below follows from that, and each is enforced by
a test, not by review.

### A new endpoint

Declare exactly one authorization intent: `.RequirePermission(...)` (all-of), `.RequireAuthenticatedOnly()`
for self-service routes, or `.AllowAnonymous()`. There is no default — an endpoint that declares
nothing is denied for everyone.

- **`EndpointAuthorizationIntentTests`** (Integration.Tests) sweeps the running host's endpoints and
  fails on any that declares none, or more than one.
- The **endpoint sweep** seeds two tenants and asks tenant A for tenant B's ids: the answer must be
  404, never 403 or 200, and list endpoints must never return B's rows. New nouns need a registry
  entry so the sweep knows how to seed one; the only way out is `[TenantSweepExempt("reason")]` at
  the mapping site, and the reason is read by a human.
- Permission constants live beside the module; register them with the permission registry. Don't
  reach for a static constants class — there isn't one.

### A new module

Five modules exist and the sixth needs a reason (ADR-0003). If you add one: a runtime project plus a
`.Contracts` project, `[AppModule]` at assembly level, and **four** registration lists to edit (API
and migrator, mediator assemblies and module assemblies). Miss one and it fails silently.

- **Architecture tests** fail if a module references another module's runtime, if a module-level
  cycle appears (including through Contracts), or if a command/paginated-query handler has no
  validator.
- Entities are tenant-isolated by default; opting out is `IGlobalEntity` and an architecture test
  watches the opt-outs. `IgnoreQueryFilters()` lives in a reviewed allow-list.

### A new job

Tenant-bound work carries only the tenant Id and runs inside that tenant. Work that belongs to no
tenant is `[SystemJob]` — and every *recurring* job must be, because the scheduler triggers it with
no tenant in scope. An unmarked job with no tenant throws at enqueue and fails at the worker.

To touch tenant data from a system job, fan out with `ITenantScope.RunForEachTenantAsync` and catch
inside the callback. Don't reach for a blanket `IgnoreQueryFilters()`: that drops the tenant filter
and makes the sweep cross-tenant again.

### A new integration event

Publish through the outbox, never the bus directly, so the event commits with the business write.
Every event is published under a tenant; one that genuinely is not must implement
`IGlobalIntegrationEvent`, or dispatch throws. Handlers never restore the tenant themselves.

### A change to the API surface

Re-export the contract and regenerate the console's types, and commit both:

```bash
bash scripts/export-openapi.sh
cd clients/console && pnpm generate:api
```

The **drift gate** re-derives both sides in CI and fails when either differs from what is committed —
including a regeneration that deletes a file. `bash scripts/check-openapi-drift.sh backend|frontend`
asks the same question locally.

### Before you push

```bash
dotnet build src/Acme.slnx -warnaserror
dotnet test src/Acme.slnx            # integration suites need Docker
cd clients/console && pnpm test && pnpm build
```

The rule files under `.agents/rules/` are the long form of everything above, one file per area.

## 4. CI, and what the repository owner must configure

**Shape**: pull requests run the gates; the push that merges them only publishes images and deploys;
a `v*` tag publishes versioned images and deploys production; `workflow_dispatch` is the escape
hatch. Path filters mean a docs-only change skips the expensive jobs while the gate job still reports
green, and every workflow cancels superseded runs and carries a timeout.

Workflows: backend (build, unit, integration, migrator image smoke, coverage floor, image publish,
optional deploy), frontend (Vitest, Playwright smoke, drift), deploy contract, gitleaks, CodeQL and
sandcastle. The brand gate and the template smoke stay behind in the template repository — they
prove the template, not your product.

Images go to GHCR as `<name>-api`, `<name>-db-migrator` and `<name>-console`, tagged
`dev-<sha>` / `dev-latest` from `develop` and `<version>` from a `v*` tag. Never a bare `latest`.

**GitHub settings the owner must set by hand** — CI is written for them and stays skipped or red
until they exist:

- **Code scanning** enabled, or the CodeQL upload fails.
- Environments **`staging`** and **`production`**, each with `DOKPLOY_URL`, `DOKPLOY_API_KEY`,
  `DOKPLOY_COMPOSE_ID` and `API_BASE_URL`. The deploy step skips until they are set.
- **`packages: write`** for `GITHUB_TOKEN`, so the image publish can push to GHCR.
- **Branch protection** on `develop` (and `main`) requiring the gate checks.

Branching is gitflow (ADR-0007): branch from `develop`, PR into `develop`; `main` takes only
`release/*` and `hotfix/*` merges, each tagged `vX.Y.Z`.

## 5. Deploy

[`docs/deploy-dokploy.md`](deploy-dokploy.md) takes a blank server to a healthy HTTPS deployment: two
pull-only stacks (data-services and app) on one Dokploy server, the 29-key environment contract, and
the fail-closed deploy script. Staging tracks `develop`, production tracks `v*` tags on `main`, and
both share the same compose files with different values.

## 6. The sandcastle pipeline

An agent pipeline that takes `ready-for-agent` issues and produces PRs. Everything repo-specific
lives in one file, `sandcastle.config.mts` — project name, issue label and queries, branch names,
gate commands, models, limits. Everything under `.sandcastle/` is generic.

```bash
pnpm install
pnpm sandcastle --dry-run     # resolves the config, prints the gates and the issues it would see
pnpm test:sandcastle          # the pipeline's own suite
```

Labels are the five triage labels (see [`docs/agents/triage-labels.md`](agents/triage-labels.md));
only `ready-for-agent` launches work, so apply it only when a human has judged the ticket fully
specified. Blocking uses GitHub's native issue dependencies and the planner fails closed if it cannot
read them.

Scaffold with `--sandcastle false` if you don't want any of this.

## 7. Known limits

Stated plainly, because each is a deliberate trade rather than an oversight.

- **`sid` is issued but not validated per request.** Revoking a session stops *refresh* immediately
  and stops API access only when the current access token expires (default 30 minutes). A per-request
  session lookup would put a database read on every call. Shorten the access-token lifetime if you
  need a tighter bound.
- **A public file URL outlives a visibility change.** Public Files assets are presigned per read
  (default 5 minutes, clamped 1–15), so flipping Public → Private stops issuance at once, but a link
  already handed out works until its signature expires. Hard revocation means deleting the object.
- **Three React Compiler lint rules sit at `warn`** in the console (`set-state-in-effect`,
  `static-components`, `refs`). Each needs a real design change, not a mechanical fix; they are left
  visible rather than disabled, and no `eslint-disable` comment exists in `src/`.
- **TypeScript is held below 7** (`^6.0.3`) across the console and the root pnpm project. Bump
  deliberately, not with a dependency batch.
- **Production refuses the Local storage provider.** It defaults to `s3` and fails fast on `local`,
  which would serve files from `wwwroot` with no signing; overriding that needs an explicit
  `Storage:AllowLocalProviderInProduction=true`. Not a limit so much as a door that is locked from
  the inside — don't unlock it to get a deployment green.
- **Per-tenant databases** (a dedicated connection string per tenant) are supported and tested today:
  the migrator, the tenant sweeps and the outbox dispatcher all follow the tenant's connection.
  Whether they *stay* supported is an open decision — the machinery is not free, and a tenant with a
  dedicated database cannot yet be entered by tenant exchange.
