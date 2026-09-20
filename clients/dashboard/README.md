# Dashboard

The tenant app (ADR-0008): a React 19 + Vite SPA for a tenant's own users — their overview, files,
identity administration, audits, trash, sessions and settings, including their tenant's branding.

It is one of two clients. Platform administration — the tenant registry, impersonation grants and
the acting layer — is the operator tool's, `clients/console`, and deliberately does not exist here:
this app holds exactly one credential, the signed-in user's own.

## Run it

```bash
corepack enable                 # once: activates the pinned pnpm
pnpm install --frozen-lockfile
pnpm dev                        # → http://localhost:5173
```

The dev server proxies `/api` and `/health` to `VITE_API_BASE_URL` (default
`http://localhost:5030`), exactly as the nginx image does in production. That is not a convenience:
the refresh token is an `HttpOnly; SameSite=Strict` cookie and CORS allows no credentials
(ADR-0002), so the browser must see one origin.

Easiest full stack: `dotnet run --project src/Host/Boilerplate.AppHost` from the repository root,
which starts PostgreSQL, Valkey, MinIO, Mailpit, the migrator, the API and both clients.

## The API contract

There are **no hand-written API types**. `clients/openapi/v1.json` is exported from the API and
checked in; the types come from it:

```bash
bash scripts/export-openapi.sh   # repo root: re-export the contract (needs no database)
pnpm generate:api                # contract → src/api/schema.d.ts
```

Both artifacts are committed, and CI fails if either is stale. Call the API through the typed client:

```ts
import { api, unwrap } from "@/lib/api-client";

const user = unwrap(await api.GET("/api/v1/identity/users/{id}", { params: { path: { id } } }));
```

## One credential, always

The access token is in `localStorage` under `boilerplate.dashboard.*`; the refresh token is an
`HttpOnly; SameSite=Strict` cookie this code never sees (ADR-0002), which is why the app is served
from the API's origin. There is no acting store, no `X-Console-As-Operator` sentinel and no
impersonation stash — a request here is always sent as the person who signed in. Acting as another
user, in any tenant, is done in the console.

## Scripts

| Script | What it does |
|---|---|
| `pnpm dev` | Vite dev server on port 5173 |
| `pnpm build` | `tsc -b && vite build` — the typecheck + bundle gate |
| `pnpm test` | Vitest units (jsdom), beside the source |
| `pnpm test:e2e` | Playwright smoke suite: sign-in and user CRUD (`--workers=1`) |
| `pnpm lint` | ESLint |
| `pnpm generate:api` | Regenerate `src/api/schema.d.ts` from the checked-in contract |

## The image

`docker build clients/dashboard` produces an nginx image configured at container start:

| Variable | Required | Meaning |
|---|---|---|
| `APP_API_URL` | yes | Origin nginx proxies `/api` and `/health` to. Server-side, never seen by the browser. |
| `APP_STORAGE_URL` | no | Object-storage origin, named in the Content-Security-Policy for presigned uploads and images. |
| `APP_DEFAULT_TENANT` | no (`root`) | Tenant identifier the sign-in form pre-fills. |
| `APP_RESOLVER` | no (`127.0.0.11`) | DNS nginx re-resolves `APP_API_URL` with on every request. The default is Docker's embedded resolver. |

The entrypoint renders `/config.json`, the nginx site and a strict CSP from those, so one built image
promotes across every environment.

The image runs nginx as **uid 101, non-root** (`nginxinc/nginx-unprivileged`), so it listens on
**8080** rather than the privileged `:80` — that is the port `docker-compose.yml` publishes and the
one Traefik's `loadbalancer.server.port` names in `deploy/dokploy/app.compose.yml`.

`APP_API_URL` reaches `proxy_pass` through a variable, which makes nginx resolve it per request
instead of once at start-up: the dashboard comes up whether or not the API is already running, and it
follows the API to a new container IP after a redeploy.
