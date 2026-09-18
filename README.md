# Boilerplate

A production-ready starter for multi-tenant SaaS: a modular .NET 10 monolith (vertical slices,
CQRS via a source-generated mediator, EF Core 10 on PostgreSQL) plus two React 19 clients, wired
for local orchestration with .NET Aspire.

`Boilerplate` is the placeholder root name. A new product renames it in one command
(`dotnet new saas -n Acme`) — see [`docs/adr/0001-placeholder-namespace-and-dotnet-new-rename.md`](docs/adr/0001-placeholder-namespace-and-dotnet-new-rename.md).

## What's in the box

- **Modules** (bounded contexts, each with a `.Contracts` project as its only public surface):
  Identity, Multitenancy, Billing, Catalog, Tickets, Chat, Files, Webhooks, Auditing, Notifications.
- **BuildingBlocks**: core domain primitives, persistence, web pipeline, caching (HybridCache on
  Valkey), eventing (outbox/inbox), jobs (Hangfire), storage (S3/MinIO), mailing, quotas.
- **Hosts**: `Boilerplate.Api` (composition root), `Boilerplate.DbMigrator` (one-shot migrate/seed —
  the API never migrates at startup), `Boilerplate.AppHost` (Aspire orchestrator).
- **Clients**: `clients/admin` (operator console) and `clients/dashboard` (tenant app) — React 19 +
  Vite + TypeScript, TanStack Query, React Router, Radix + Tailwind, SignalR/SSE.
- **Deploy**: Docker Compose (`deploy/docker`) and Terraform for AWS (`deploy/terraform`).

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download) (pinned in `global.json`)
- [Docker](https://www.docker.com/) — Postgres, Valkey and MinIO are started by Aspire, and the
  integration tests use Testcontainers
- [Node 20+](https://nodejs.org/) for the React clients

## Run

```bash
dotnet run --project src/Host/Boilerplate.AppHost
```

The Aspire dashboard is at <https://localhost:15888>; the API and its Scalar reference at
<https://localhost:7030/scalar>; admin at <http://localhost:5173>; dashboard at
<http://localhost:5174>. The migrator seeds demo tenants and accounts on first run.

## Test

```bash
dotnet test src/Boilerplate.slnx          # unit + architecture + Testcontainers integration
cd clients/admin     && npm run test:e2e  # Playwright
cd clients/dashboard && npm run test:e2e  # Playwright
```

## Repository layout

| Path | What |
|---|---|
| `src/BuildingBlocks/` | Shared framework libraries |
| `src/Modules/{Name}/` | Bounded contexts (runtime project + `.Contracts`) |
| `src/Host/` | API, AppHost, DbMigrator, Migrations |
| `src/Tests/` | Unit, architecture (NetArchTest) and integration (Testcontainers) tests |
| `src/Tools/CLI` | Scaffolding CLI |
| `clients/` | The two React apps |
| `deploy/` | Docker Compose, Terraform, Dokploy |
| `docs/adr/` | Architecture decision records |

## Contributing

See [`CONTRIBUTING.md`](CONTRIBUTING.md). Branch from and target `develop`; `main` only receives
release and hotfix merges (ADR-0007).

## License

MIT — see [`LICENSE`](LICENSE).
