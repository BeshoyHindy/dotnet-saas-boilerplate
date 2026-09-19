# Console

The operator tool (ADR-0008): a React 19 + Vite SPA for root operators — the tenant registry,
impersonation grants, the acting layer ("enter tenant" / "impersonate"), the identity screens an
operator needs while acting, cross-tenant audits, health and sessions.

It is one of two clients. The product a tenant's own users sign in to is `clients/dashboard`;
tenant self-service (My Files, Trash, one's own branding) lives there, not here. A signed-in user
without an operator permission is shown one plain "this console is for platform operators" screen
rather than a shell whose every panel would 403 (`src/auth/operator-gate.tsx`).

## Run it

```bash
corepack enable                 # once: activates the pinned pnpm
pnpm install --frozen-lockfile
pnpm dev                        # → http://localhost:5174
```

The dev server proxies `/api` and `/health` to `VITE_API_BASE_URL` (default
`http://localhost:5030`), exactly as the nginx image does in production. That is not a convenience:
the refresh token is an `HttpOnly; SameSite=Strict` cookie and CORS allows no credentials
(ADR-0002), so the browser must see one origin.

Easiest full stack: `dotnet run --project src/Host/Boilerplate.AppHost` from the repository root,
which starts PostgreSQL, Valkey, MinIO, Mailpit, the migrator, the API and this console.

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

## Acting as someone else

Two ways in (ADR-0002, issue #9), one credential, and it is **never stored**:

| | Endpoint | Who |
|---|---|---|
| Enter a tenant | `POST /api/v1/identity/operator/token-exchange` | root operators (`Permissions.Platform.Users.Impersonate`) |
| Impersonate a user | `POST /api/v1/identity/impersonation/start` | same tenant only |
| Stop | `POST /api/v1/identity/impersonation/end` | returns **no token** |

The short-lived, access-only token lives in module memory (`src/auth/acting-store.ts`), beside the
signed-in session it never replaces. So a reload drops you back into your own account, a 401 on it
means "revoked or expired" rather than "refresh me", and it cannot be read out of localStorage. While
it is installed, `ActingBanner` says so on every page and the credential screens (2FA, change
password) are disabled — the API refuses them from an actor anyway.

## Scripts

| Script | What it does |
|---|---|
| `pnpm dev` | Vite dev server on port 5174 |
| `pnpm build` | `tsc -b && vite build` — the typecheck + bundle gate |
| `pnpm test` | Vitest units (jsdom), beside the source |
| `pnpm test:e2e` | Playwright smoke suite: sign-in, user CRUD, operator enters a tenant |
| `pnpm lint` | ESLint |
| `pnpm generate:api` | Regenerate `src/api/schema.d.ts` from the checked-in contract |

## The image

`docker build clients/console` produces an nginx image configured at container start:

| Variable | Required | Meaning |
|---|---|---|
| `APP_API_URL` | yes | Origin nginx proxies `/api` and `/health` to. Server-side, never seen by the browser. |
| `APP_STORAGE_URL` | no | Object-storage origin, named in the Content-Security-Policy for presigned uploads and images. |
| `APP_DEFAULT_TENANT` | no (`root`) | The tenant operators sign in to. There is no tenant field on this app's form. |
| `APP_DASHBOARD_URL` | no | Where the tenant app is deployed. Only used to link a non-operator who signed in here to the app that is theirs; empty hides the link. |
| `APP_RESOLVER` | no (`127.0.0.11`) | DNS nginx re-resolves `APP_API_URL` with on every request. The default is Docker's embedded resolver. |

The entrypoint renders `/config.json`, the nginx site and a strict CSP from those, so one built image
promotes across every environment.

The image runs nginx as **uid 101, non-root** (`nginxinc/nginx-unprivileged`), so it listens on
**8080** rather than the privileged `:80` — that is the port `docker-compose.yml` publishes and the
one Traefik's `loadbalancer.server.port` names in `deploy/dokploy/app.compose.yml`.

`APP_API_URL` reaches `proxy_pass` through a variable, which makes nginx resolve it per request
instead of once at start-up: the console comes up whether or not the API is already running, and it
follows the API to a new container IP after a redeploy.
