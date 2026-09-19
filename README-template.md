# Boilerplate

Your application — a production-ready modular .NET 10 monolith with two React 19 apps,
multitenancy, identity, background jobs, and cloud-native deploy.

You **own all of this source**. There are no framework NuGet packages to track or upgrade —
the shared code lives in `src/BuildingBlocks` and is yours to change.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [Node.js 20+](https://nodejs.org) — for the React apps
- [Docker](https://www.docker.com/) — Postgres, Redis, MinIO (orchestrated by Aspire)

## Quick start

### Everything at once (recommended) — .NET Aspire

```bash
dotnet run --project src/Host/Boilerplate.AppHost
```

Aspire starts Postgres, Redis, and MinIO, runs database migrations, then launches the API
**and both React apps**.

| Surface | URL |
|---|---|
| Aspire dashboard | https://localhost:15888 |
| API + Scalar docs | https://localhost:7030/scalar |
| Admin console | http://localhost:5173 |
| Tenant dashboard | http://localhost:5174 |

### Backend only

```bash
dotnet run --project src/Host/Boilerplate.Api      # needs external Postgres + Redis
```

### Frontend only (against a running API)

```bash
cd clients/console && pnpm install && pnpm dev      # → http://localhost:5173
```

The React apps read their API URL at runtime from `public/config.json` — no rebuild to repoint.

## Project structure

```
src/
  BuildingBlocks/      Shared framework libraries — yours to modify
  Modules/             Bounded contexts: Identity, Multitenancy, Auditing,
                       Files, Notifications
  Host/
    Boilerplate.Api/                    API composition root
    Boilerplate.AppHost/                .NET Aspire orchestrator
    Boilerplate.DbMigrator/             One-shot migrate / seed runner
    Boilerplate.Migrations.PostgreSQL/  EF Core migrations
  Tests/               Unit, integration (Testcontainers), and architecture tests
clients/
  admin/               Operator console (React 19 + Vite + Tailwind)
  dashboard/           Tenant app (React 19 + Vite + Tailwind)
docker-compose.yml     Runs the production images locally (+ .env.example)
deploy/
  dokploy/             Dokploy deployment configuration
```

## Database

Migrations run automatically under Aspire. To apply them yourself:

```bash
dotnet run --project src/Host/Boilerplate.DbMigrator -- apply --seed
```

## Make it yours — first-run checklist

This project shipped with sensible defaults. Before production:

- [ ] **Secrets** — the values `scripts/local-env.sh` writes into `.env` are local-only
      throwaways. Generate fresh ones for anything deployed, and never commit `.env`.
- [ ] **Branding** — the clients render a plain text wordmark; swap it (and add a logo under
      `clients/*/public/`) in `clients/*/src/components/**` for your own identity.
- [ ] **Mail** — configure SMTP / SendGrid under `MailOptions` in
      `src/Host/Boilerplate.Api/appsettings.json`.
- [ ] **OpenAPI contact** — update `OpenApiOptions.Contact` in `appsettings.json`.
- [ ] **Container registry & infra** — set your registry and review bucket / database names
      in `deploy/dokploy`.

## Run the container images (Docker Compose)

```bash
bash scripts/local-env.sh         # once — writes .env with generated secrets
docker compose up --build         # API :8080, console :8081, Mailpit inbox :8025
curl -fsS http://localhost:8080/health/ready
```

This runs the same `api` / `migrator` images a deployment uses, against PostgreSQL, Valkey,
MinIO and a Mailpit mail catcher. The containers run as Production, so placeholder secrets and
`AllowedHosts: *` are refused exactly as they would be on a server — hence the generated `.env`.
Everything else has a local default; see `.env.example`.

Sign in to the console as `admin@root.com` using the `SEED_ADMIN_PASSWORD` from `.env`, then
rotate it from Settings → Security.

## Adding a feature

1. Contracts command/query in `src/Modules/{Module}.Contracts/v1/{Area}/{Feature}/`
2. Handler + FluentValidation validator in `src/Modules/{Module}/Features/...`
3. Endpoint, wired into the module's `MapEndpoints()`
4. Tests

## Running tests

```bash
dotnet test src/Boilerplate.slnx       # integration tests require Docker
```

## Learn more

- Architecture decision records live in `docs/adr/`.
- Agent- and contributor-facing conventions live in `AGENTS.md` and `.agents/`.
