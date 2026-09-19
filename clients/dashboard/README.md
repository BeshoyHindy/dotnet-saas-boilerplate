# Boilerplate — Dashboard

Tenant-facing dashboard for the Boilerplate. Shows the tenant's validity window and recent activity.

Built with React 19, Vite 7, TypeScript, TanStack Query, React Router, Tailwind 4 + shadcn/ui, and Recharts. Standalone — not part of a pnpm workspace — so it plugs into .NET Aspire as a plain `ExecutableResource`.

## Prerequisites

- Node.js 20+
- The API running (locally or remote)

## Install & run

Two options — pick whichever matches how you want to develop.

### Option A — run everything through Aspire (recommended)

The AppHost launches Postgres, Redis, MinIO, the API, the admin app, **and** this dashboard together, with `VITE_API_BASE_URL` wired via service discovery.

```bash
npm install --prefix clients/dashboard   # one-time
dotnet run --project src/Host/Boilerplate.AppHost
```

Aspire dashboard exposes `boilerplate-dashboard` on <http://localhost:5174>.

### Option B — run the frontend standalone

Useful when the API is already running elsewhere.

```bash
cd clients/dashboard
npm install
npm run dev          # http://localhost:5174
```

The dev server proxies `/api`, `/openapi`, and `/scalar` to `VITE_API_BASE_URL` (default `http://localhost:5030`).

## Scripts

| Script            | Purpose                              |
|-------------------|--------------------------------------|
| `npm run dev`     | Vite dev server on port 5174         |
| `npm run build`   | `tsc -b` + `vite build` → `dist/`    |
| `npm run preview` | Preview the production build         |
| `npm run lint`    | ESLint (flat config)                 |

## Configuration

| Variable              | Default                  | Purpose                                       |
|-----------------------|--------------------------|-----------------------------------------------|
| `VITE_API_BASE_URL`   | `http://localhost:5030`  | API origin used by the dev proxy              |
| `VITE_DEFAULT_TENANT` | `root`                   | Default tenant header for unauthenticated calls |

## Architecture

```
src/
├── api/                  # Typed API clients (tenants, audits, files, health, notifications)
├── auth/                 # JWT-backed auth (own localStorage prefix: boilerplate.dashboard.*)
├── components/
│   ├── layout/           # Sidebar, Topbar, AppShell
│   └── ui/               # shadcn primitives
├── lib/                  # api-client, query-client, cn
├── pages/                # Overview, Login, NotFound
├── styles/globals.css    # Tailwind 4 CSS-first + shadcn variables
├── App.tsx, main.tsx, routes.tsx
```

### What the overview shows

- **Valid for** — the tenant's validity window (days left / grace / expired) from `GET /api/v1/tenants/me/status`.
- **Recent audits** — the last 24 hours of audit events from `GET /api/v1/audits`.

## Authentication flow

Identical to the admin app: JWT in `localStorage`, `Authorization: Bearer` + `tenant` headers, single-flight refresh on 401 via `POST /api/v1/identity/token/refresh`. Keys are namespaced `boilerplate.dashboard.*` so both apps can run side-by-side without clobbering each other's session.

## Production build

`npm run build` emits `dist/`. Deploy behind any static host; forward `/api/*` to the backend and serve `index.html` as the SPA fallback.
