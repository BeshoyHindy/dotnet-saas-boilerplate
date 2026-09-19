# Boilerplate

Your application — a production-ready modular .NET 10 monolith with multitenancy, identity,
background jobs, and a cloud-native deploy.

You **own all of this source**. There are no framework NuGet packages to track or upgrade —
the shared code lives in `src/BuildingBlocks` and is yours to change.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
<!--#if (frontend) -->
- [Node.js 20+](https://nodejs.org) — for the React apps
<!--#endif -->
- [Docker](https://www.docker.com/) — Postgres, Redis, MinIO

## Quick start

<!--#if (aspire) -->
### Everything at once (recommended) — .NET Aspire

```bash
dotnet run --project src/Host/Boilerplate.AppHost
```

<!--#if (frontend) -->
Aspire starts Postgres, Redis, and MinIO, runs database migrations, then launches the API
**and the React console**.
<!--#else -->
Aspire starts Postgres, Redis, and MinIO, runs database migrations, then launches the API.
<!--#endif -->

| Surface | URL |
|---|---|
| Aspire dashboard | https://localhost:15888 |
| API + Scalar docs | https://localhost:7030/scalar |
<!--#if (frontend) -->
| Console | http://localhost:5173 |
<!--#endif -->

<!--#endif -->
### Backend only

```bash
dotnet run --project src/Host/Boilerplate.Api      # needs external Postgres + Redis
```

<!--#if (frontend) -->
### Frontend only (against a running API)

```bash
cd clients/console && pnpm install && pnpm dev      # → http://localhost:5173
```

The console reads its API URL at runtime from `public/config.json` — no rebuild to repoint.
Types come from the checked-in `clients/openapi/v1.json`; regenerate it with
`bash scripts/export-openapi.sh` after changing an endpoint (ADR-0004).
<!--#endif -->

## Project structure

```
src/
  BuildingBlocks/      Shared framework libraries — yours to modify
  Modules/             Bounded contexts: Identity, Multitenancy, Auditing,
                       Files, Notifications
  Host/
    Boilerplate.Api/                    API composition root
<!--#if (aspire) -->
    Boilerplate.AppHost/                .NET Aspire orchestrator
<!--#endif -->
    Boilerplate.DbMigrator/             One-shot migrate / seed runner
    Boilerplate.Migrations.PostgreSQL/  EF Core migrations
  Tests/               Unit, integration (Testcontainers), and architecture tests
<!--#if (frontend) -->
clients/
  console/             The React 19 + Vite + Tailwind console (pnpm)
  openapi/v1.json      The checked-in API contract the console types from
<!--#endif -->
docker-compose.yml     Runs the production images locally (+ .env.example)
deploy/
  dokploy/             Dokploy deployment configuration
docs/adr/              Architecture decision records
```

## Database

<!--#if (aspire) -->
Migrations run automatically under Aspire. To apply them yourself:
<!--#else -->
The API never migrates at startup. To apply migrations:
<!--#endif -->

```bash
dotnet run --project src/Host/Boilerplate.DbMigrator -- apply --seed
```

## Make it yours — first-run checklist

This project shipped with sensible defaults. Before production:

- [ ] **Shell scripts** — `dotnet new` copies file content but not the POSIX executable
      bit, so run `chmod +x scripts/*.sh deploy/dokploy/*.sh deploy/dokploy/tests/*.sh`
      once (everything here invokes them as `bash <script>` either way).
- [ ] **Secrets** — the values `scripts/local-env.sh` writes into `.env` are local-only
      throwaways. Generate fresh ones for anything deployed, and never commit `.env`.
<!--#if (frontend) -->
- [ ] **Branding** — the console renders a plain text wordmark; swap it (and add a logo under
      `clients/console/public/`) in `clients/console/src/components/**` for your own identity.
<!--#endif -->
- [ ] **Mail** — configure SMTP / SendGrid under `MailOptions` in
      `src/Host/Boilerplate.Api/appsettings.json`.
- [ ] **OpenAPI contact** — update `OpenApiOptions.Contact` in `appsettings.json`.
- [ ] **Container registry & infra** — set your registry and review bucket / database names
      in `deploy/dokploy`.
- [ ] **CI** — `.github/workflows/` runs the gates on pull requests into `develop` and `main`,
      and publishes images to GHCR. The Dokploy deploy step stays skipped until you set
      `DOKPLOY_URL`, `DOKPLOY_API_KEY` and `DOKPLOY_COMPOSE_ID` on the environments.
- [ ] **License** — this scaffold ships without one. Add the license your product needs;
      `CONTRIBUTING.md` points at it.
- [ ] **Agent conventions** — `AGENTS.md` and `.agents/rules/` describe how this codebase
      expects to be extended. Keep them current; the coding agents read them first.

## Run the container images (Docker Compose)

```bash
bash scripts/local-env.sh         # once — writes .env with generated secrets
<!--#if (frontend) -->
docker compose up --build         # API :8080, console :8081, Mailpit inbox :8025
<!--#else -->
docker compose up --build         # API :8080, Mailpit inbox :8025
<!--#endif -->
curl -fsS http://localhost:8080/health/ready
```

<!--#if (frontend) -->
This runs the same `api` / `migrator` / console images a deployment uses, against PostgreSQL,
Valkey, MinIO and a Mailpit mail catcher.
<!--#else -->
This runs the same `api` / `migrator` images a deployment uses, against PostgreSQL, Valkey,
MinIO and a Mailpit mail catcher.
<!--#endif -->
The containers run as Production, so placeholder secrets and
`AllowedHosts: *` are refused exactly as they would be on a server — hence the generated `.env`.
Everything else has a local default; see `.env.example`.

<!--#if (frontend) -->
Sign in to the console as `admin@root.com` using the `SEED_ADMIN_PASSWORD` from `.env`, then
rotate it from Settings → Security.
<!--#else -->
The seeded root admin is `admin@root.com` with the `SEED_ADMIN_PASSWORD` from `.env`. Rotate it
after the first sign-in.
<!--#endif -->

## Adding a feature

1. Contracts command/query in `src/Modules/{Module}.Contracts/v1/{Area}/{Feature}/`
2. Handler + FluentValidation validator in `src/Modules/{Module}/Features/...`
3. Endpoint, wired into the module's `MapEndpoints()`
4. Tests

## Running tests

```bash
dotnet test src/Boilerplate.slnx       # integration tests require Docker
<!--#if (frontend) -->
cd clients/console && pnpm test:e2e    # Playwright, route-mocked
<!--#endif -->
<!--#if (sandcastle) -->
pnpm install && pnpm test:sandcastle   # the agent orchestrator's own suite
<!--#endif -->
```

## Learn more

- Architecture decision records live in `docs/adr/`.
- Per-area conventions (modules, database, eventing, testing, security) live in
  `.agents/rules/`; `AGENTS.md` is the entry point coding agents read first.
- The deployment runbook is `docs/deploy-dokploy.md`.
<!--#if (sandcastle) -->
- The agent pipeline lives in `.sandcastle/`; everything project-specific about it is in
  `sandcastle.config.mts` (ADR-0006).
<!--#endif -->
