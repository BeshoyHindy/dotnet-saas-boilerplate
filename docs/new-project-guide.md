# New project guide

From an empty machine to a running product, and then to the rules that keep it multi-tenant.

Read [`CONTEXT.md`](../CONTEXT.md) first — one page, and it fixes the vocabulary the rest of this
guide uses.

## 1. Scaffold

Already inside a project that was scaffolded from the template? Skip to §2.

The template repository's root *is* the template. Nothing is published to NuGet.org, so a clone plus
a local install is the whole distribution:

```bash
git clone <template repository> dotnet-saas-template && cd dotnet-saas-template
dotnet new install .                 # installs from this working directory
dotnet new saas -n Contoso -o ../Contoso
```

`-n` names the product: the template's placeholder name is replaced by `Contoso` in namespaces,
project and file names, and a derived lowercase form (`contoso`) renames image names, database and
bucket names, the compose project, npm scopes, the JWT issuer and audience and each client's
`localStorage` prefixes. Pick a name that is not one of the demo tenants below (`acme`, `globex`),
or seeded data and renamed output become hard to tell apart.

| Parameter | Default | What `false` drops |
|---|---|---|
| `--frontend` | `true` | `clients/dashboard` and `clients/console` (BOTH clients), their services in `docker-compose.yml` and the app stack, both images, the client CI workflow, ADR-0004 + ADR-0008. The API contract (`clients/openapi/v1.json`) and its backend drift gate stay: the API still has consumers. |
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
`.agents/workflows/` with `skills-lock.json`, the template repository's own gates and research
notes, and its record of how the placeholder name works.

Uninstall the template with `dotnet new uninstall <path>` when you are done.

## 2. First run

A scaffold is not a git repository yet, and the scripts need one: they resolve paths from the
repository root and refuse to run outside it. Do these three steps first, in this order:

```bash
git init -b develop
chmod +x scripts/*.sh deploy/dokploy/*.sh deploy/dokploy/tests/*.sh
git add -A && git commit -m "chore: scaffold"
```

`dotnet new` copies file content but not the POSIX executable bit, and the deploy contract gate
(`deploy/dokploy/tests/run.sh`) asserts that bit — so `chmod` comes *before* the first commit, or the
scripts land in git as `100644` and the gate fails in CI too. Every other script works when invoked
as `bash <script>`, which is how the docs spell it. `develop` is the integration branch (ADR-0007);
from here on, work on `feature/<slug>` branches.

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
dotnet run --project src/Host/Boilerplate.AppHost
```

<!--#if (frontend) -->
Aspire starts PostgreSQL, Valkey, RustFS and Mailpit, runs the migrator to completion, then the API,
then both clients.
<!--#else -->
Aspire starts PostgreSQL, Valkey, RustFS and Mailpit, runs the migrator to completion, then the API.
<!--#endif -->
The Aspire dashboard is at <https://localhost:15888>; it also shows the generated
object-store secret key, seeded-admin and demo passwords (Resources → Parameters).

#### Running two AppHosts at once

Every container the AppHost starts (PostgreSQL, Valkey, RustFS, Mailpit) lets Aspire allocate its
host port, so a second checkout or worktree can run its own stack alongside yours. Read the actual
addresses off the dashboard; nothing in the stack hard-codes them, and everything that needs one —
the API's `Storage__S3__*`, `storage-init`, the SMTP host — takes it from an endpoint reference.

What is still pinned, deliberately: the API (`7030`/`5030`, from `launchSettings.json`, quoted by
the `.http` request files and the devcontainer) and the Aspire dashboard (`15888`).
<!--#if (frontend) -->
So are the two client dev servers, `5173` (dashboard) and `5174` (console): the Vite proxy origin is
what makes the `SameSite=Strict` refresh cookie work.
<!--#endif -->
Those clash loudly and instantly, not silently, so two *full* instances still need one of them to be
stopped.

**Starting the database from empty.** The AppHost keeps PostgreSQL's data in the
`boilerplate-postgres-data-v2` volume. Stop the AppHost, remove the PostgreSQL container (see
*Known limits*: it outlives the AppHost), then `docker volume rm boilerplate-postgres-data-v2`.
Destructive, and it is local development data.

### Or: the container images

```bash
bash scripts/local-env.sh            # once — writes a gitignored .env with generated secrets
docker compose up --build
curl -fsS http://localhost:8080/health/ready
```

These are the same images a deployment runs, and they run as **Production**, so the production
fail-fast guards apply: no placeholder secret, no `AllowedHosts: *`. That is why the secrets have no
defaults and the script generates them. `docker compose down -v` resets the data.

The one exception is the `migrator` service, which runs as Development: it is asked to seed the demo
accounts below, and demo seeding is refused in a Production host. Remove `--demo` and that
`DOTNET_ENVIRONMENT` line together if you want the migrator on Production too.

<!--#if (frontend) -->
### A client on its own

```bash
cd clients/dashboard && pnpm install && pnpm dev    # http://localhost:5173  (tenant app)
cd clients/console   && pnpm install && pnpm dev    # http://localhost:5174  (operator tool)
```

The dev server proxies `/api`, `/openapi`, `/scalar` and `/health` to the API (target from
`VITE_API_BASE_URL`, default `http://localhost:5030`). **Keep it that way.** The refresh token is an
`HttpOnly; SameSite=Strict` cookie and CORS allows no credentials, so a client served from a
different origin than the API can never refresh a session. The runtime `apiBase` is `""` — same
origin — in every environment, and the nginx image proxies exactly like the dev server does. A CORS
error is a proxy misconfiguration, not a reason to point the client elsewhere.

Sign in to the console as the seeded root admin (`admin@root.com`) with the password from the Aspire
dashboard or `.env` (`SEED_ADMIN_PASSWORD`), and rotate it. Both clients sign in through
`POST /api/v1/tenants/{tenant}/auth/token`; `src/Host/Boilerplate.Api/Requests/Identity/identity-token.http`
calls it directly once you paste the password into its `@adminPassword` variable.
<!--#else -->
### Signing in

This product ships no client, so sign in through the API: `POST /api/v1/tenants/root/auth/token`
as the seeded root admin (`admin@root.com`) with the password from the Aspire dashboard or `.env`
(`SEED_ADMIN_PASSWORD`), and rotate it.
`src/Host/Boilerplate.Api/Requests/Identity/identity-token.http` makes that call once you paste the
password into its `@adminPassword` variable. Mailed links (password reset, email confirmation) carry
the API's own origin until you set `OriginOptions__OriginUrl` to the front end you build.
<!--#endif -->

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

**When you start a real product**, delete `src/Host/Boilerplate.DbMigrator/DemoSeed/` along with the
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
  entry so the sweep knows how to seed one; the only way out is `.ExemptFromTenantSweep("reason")`
  on the endpoint at its mapping site, and the reason is read by a human.
- Permission constants live in the module's Contracts project, in
  `Modules.{X}.Contracts/Authorization/{X}Permissions.cs`: a `public static class` with one nested
  class per resource (a `Resource` string plus one `Permissions.{Resource}.{Action}` constant per
  action) and an `All` list of `AppPermission`s, which the module registers with
  `builder.Services.AddPermissions({X}Permissions.All)`. Prefix a new resource with the module
  (`"Notifications.Inbox"`) so it cannot collide with another module's. `IsBasic: true` grants the
  permission to the Basic role too; `IsRoot: true` reserves it for the root tenant's Admin. Everything
  else goes to Admin only — the role-permission sync grants a newly registered permission to every
  tenant's Admin role on the next start, so there is no migration or seed step to write.

### A new product module

The five modules the template ships are **platform modules** (ADR-0003). Your product's own nouns go
in **product modules** (ADR-0010). Add one for a new bounded context, not for a new feature: a
tenant-scoped `Note` starts a `Notes` module, and the next Note feature is a slice inside it. A
product module may use a platform module's `.Contracts`; a platform module never references a
product module.

A module is a runtime project plus a `.Contracts` project, with `[AppModule]` at assembly level.
Product modules take order 1000 and up, in steps of 100 (`[assembly: AppModule(typeof(NotesModule),
1000)]`), so they start after the platform modules. Then make seven edits:

1. `src/Boilerplate.slnx`: the runtime project.
2. `src/Boilerplate.slnx`: the `.Contracts` project.
3. `src/Host/Boilerplate.Migrations.PostgreSQL/Boilerplate.Migrations.PostgreSQL.csproj`: a
   `ProjectReference` to the runtime project, plus its `<Folder Include="{Module}\" />` for the
   migrations. This reference is how both hosts reach the module.
4. `src/Host/Boilerplate.Api/HostModules.cs`: `typeof({Module}Module).Assembly`.
5. `src/Host/Boilerplate.Api/Program.cs`: two entries in the `o.Assemblies` list of `AddMediator`,
   a type from the `.Contracts` assembly and one from the runtime assembly.
6. `src/Host/Boilerplate.DbMigrator/HostModules.cs`: as in 4.
7. `src/Host/Boilerplate.DbMigrator/Program.cs`: as in 5.

The `o.Assemblies` lists stay literal because Mediator's source generator reads them as written.
**`HostModuleListTests`** (Architecture.Tests) fails when an `[AppModule]` assembly is missing from
any of the four host lists, and names the list and the file to edit. Give the module its own rule
file, `.agents/rules/modules/<name>.md`, like the five platform modules have.

- **Architecture tests** fail if a module references another module's runtime, if a module-level
  cycle appears (including through Contracts), if a platform module references a product module
  (`PlatformModuleDirectionTests`), or if a command/paginated-query handler has no validator.
- Entities are tenant-isolated by default; opting out is `IGlobalEntity` and an architecture test
  watches the opt-outs. `IgnoreQueryFilters()` lives in a reviewed allow-list.
- **Tests**: integration tests are a product module's default, in `src/Tests/Integration.Tests/Tests/{Module}/`.
  Each new noun gets a tenant-sweep entry (a `ResourceKind` and a seeder in `TenantSweepSeeder`),
  list-only nouns included; see `.agents/rules/integration-testing.md`. A unit-test project is
  optional. If you want one: create `src/Tests/{Module}.Tests/Boilerplate.{Module}.Tests.csproj`
  (copy `Files.Tests`' and point its references at your module), add it to `src/Boilerplate.slnx`
  under `/Tests/`, and add `[assembly: InternalsVisibleTo("Boilerplate.{Module}.Tests")]` to the
  module's `AssemblyInfo.cs`.

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

<!--#if (frontend) -->
Re-export the contract and regenerate BOTH clients' types, and commit every artifact:

```bash
bash scripts/export-openapi.sh
cd clients/dashboard && pnpm generate:api
cd clients/console   && pnpm generate:api
```

The **drift gate** re-derives both sides in CI and fails when either differs from what is committed —
including a regeneration that deletes a file. `bash scripts/check-openapi-drift.sh backend|frontend`
asks the same question locally.
<!--#else -->
Re-export the contract and commit it:

```bash
bash scripts/export-openapi.sh
```

The **drift gate** re-derives `clients/openapi/v1.json` in CI and fails when it differs from what is
committed. `bash scripts/check-openapi-drift.sh backend` asks the same question locally.
<!--#endif -->

### Before you push

Work on a branch, never on `develop` itself (ADR-0007): `git switch -c feature/<slug> develop`
before the first change, and open the pull request into `develop`. Then run the gates:

```bash
dotnet build src/Boilerplate.slnx -warnaserror
dotnet test src/Boilerplate.slnx            # integration suites need Docker
bash scripts/check-openapi-drift.sh backend
<!--#if (frontend) -->
cd clients/dashboard && pnpm test && pnpm build
cd clients/console   && pnpm test && pnpm build
<!--#endif -->
```

The rule files under `.agents/rules/` are the long form of everything above, one file per area.

## 4. CI, and what the repository owner must configure

**Shape**: pull requests run the gates; the push that merges them only publishes images and deploys;
a `v*` tag publishes versioned images and deploys production; `workflow_dispatch` is the escape
hatch. Path filters mean a docs-only change skips the expensive jobs while the gate job still reports
green, and every workflow cancels superseded runs and carries a timeout.

Workflows: backend (build, unit, integration, migrator image smoke, coverage floor, OpenAPI drift,
image publish, optional deploy),
<!--#if (frontend) -->
frontend (Vitest, Playwright smoke, drift),
<!--#endif -->
deploy contract, gitleaks, CodeQL and sandcastle.

<!--#if (frontend) -->
Images go to GHCR as `<name>-api`, `<name>-db-migrator`, `<name>-dashboard` and `<name>-console`, tagged
<!--#else -->
Images go to GHCR as `<name>-api` and `<name>-db-migrator`, tagged
<!--#endif -->
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

- **Session revocation reaches other API replicas within 30 seconds.** Every request checks its
  token's session, so a revoked session is refused on its next request by the instance that revoked
  it. Each instance caches the answer for 30 seconds (`SessionLiveness.CacheDuration`) to keep the
  check off the database, and there is no cross-instance invalidation — so on a multi-replica
  deployment another replica may keep accepting the token for up to that long.
- **A public file URL outlives a visibility change.** Public Files assets are presigned per read
  (default 5 minutes, clamped 1–15), so flipping Public → Private stops issuance at once, but a link
  already handed out works until its signature expires. Hard revocation means deleting the object.
- **Nothing scans uploads for malware.** `IFileScanner` ships as `NoOpFileScanner`, which reports
  every file clean. What finalize does check is that the bytes are the declared type: their
  signature must match the extension and content type, and no shipped category accepts SVG or HTML
  (a category opts in with `AllowScriptCapableTypes`). That is not malware scanning. A product that
  needs it registers its own `IFileScanner` (ClamAV, a cloud scanning service) after the Files
  module, and the last registration wins; finalize calls it, and an `Infected` result leaves the
  file `Quarantined` instead of `Available`.
<!--#if (frontend) -->
- **Three React Compiler lint rules sit at `warn`** in both clients (`set-state-in-effect`,
  `static-components`, `refs`). Each needs a real design change, not a mechanical fix; they are left
  visible rather than disabled, and no `eslint-disable` comment exists in `src/`.
<!--#endif -->
<!--#if (frontend) -->
- **TypeScript is held below 7** (`^6.0.3`) across both clients and the root pnpm project. Bump
  deliberately, not with a dependency batch.
<!--#else -->
- **TypeScript is held below 7** (`^6.0.3`) in the root pnpm project. Bump deliberately, not with a
  dependency batch.
<!--#endif -->
- **Production refuses the Local storage provider.** It defaults to `s3` and fails fast on `local`,
  which would serve files from `wwwroot` with no signing; overriding that needs an explicit
  `Storage:AllowLocalProviderInProduction=true`. Not a limit so much as a door that is locked from
  the inside — don't unlock it to get a deployment green.
- **The idempotency lock is per process.** Two requests carrying the same `Idempotency-Key` are
  serialised by an in-memory lock (`KeyedAsyncLock`), so on one API replica the second waits and
  replays the first's result. Across replicas nothing coordinates them: duplicates that land on
  different instances can both execute. Safe on the default single-replica stack; before scaling the
  API out, replace the lock with a distributed lease (a Redis `SET NX`).
- **Hangfire runs a fixed 5 workers per process**, not a count scaled to the machine's cores. Change
  `WorkerCount` in `src/BuildingBlocks/Jobs/Extensions.cs` if a product's job load needs more.
- **The API image is JIT-only** (no ReadyToRun), so each new container pays a one-time cold-start
  cost while the hot paths compile. Nothing to fix for a long-running server; worth revisiting only
  if you scale to zero.
<!--#if (aspire) -->
- **Aspire's data containers outlive the AppHost.** PostgreSQL, Valkey and object storage are
  `ContainerLifetime.Persistent`, so stopping the AppHost (Ctrl+C) leaves them running, keeping
  their ports and memory — deliberately, so the next run starts in seconds with its data intact. To
  free them, `docker ps` shows them under their resource names with a generated suffix; remove them
  with `docker rm -f <container>`. Their data volumes survive that; remove a volume only when you
  want its data gone (see *Starting the database from empty* in §2).
<!--#endif -->
<!--#if (frontend) -->
- **The first `docker compose up --build` is slow.** It pulls the .NET SDK and Node base images and
  builds four images (migrator, API, dashboard, console) before anything starts — minutes on a fast
  connection, much longer on a slow one. A network timeout there is the download, not the template;
  re-run it, and later runs reuse the cache.
<!--#else -->
- **The first `docker compose up --build` is slow.** It pulls the .NET SDK base image and builds two
  images (migrator, API) before anything starts — minutes on a fast connection, much longer on a
  slow one. A network timeout there is the download, not the template; re-run it, and later runs
  reuse the cache.
<!--#endif -->
- **The API container logs `Cannot load library libgssapi_krb5.so.2` at startup.** Harmless: the
  chiseled runtime image carries no Kerberos library, and the PostgreSQL driver logs its absence
  when it probes for GSS authentication, which this stack never uses. Health still reports Healthy.
