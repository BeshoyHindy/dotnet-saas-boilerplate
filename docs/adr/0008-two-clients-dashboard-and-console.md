---
status: accepted
supersedes: 0004
---
# Two React clients: the dashboard (tenant app) and the console (operator tool)

ADR-0004 merged the operator app and the tenant app into one client, `clients/console`, on the argument that two apps double the build, test and deploy surface. That saving is real but it is the wrong trade, and this ADR reverses it. The template ships **two** clients:

- **`clients/dashboard`** — the product. What a tenant's own users sign in to: their overview, files, identity administration for their tenant, trash, sessions, their tenant's branding, their own settings. Package `@boilerplate/dashboard`, dev port 5173, image `boilerplate-dashboard`.
- **`clients/console`** — the operator tool. What a root operator signs in to: the tenant registry, entering a tenant or impersonating a user (the acting layer), the identity screens an operator needs *while acting*, cross-tenant audits, health, sessions, and their own account settings. Package `@boilerplate/console`, dev port 5174, image `boilerplate-console`.

## Why two again

**They are not the same product.** The dashboard is the thing a customer pays for and the thing every team replaces first; the console is internal tooling whose audience is a handful of people at the vendor. One app meant every tenant user downloaded, and every release re-tested, screens they must never reach — and the operator surface was kept out of their way by nothing but permission checks in the client's own nav data.

**A permission check is not a boundary.** With one app, "can this person manage tenants" decided what rendered, and the only thing between a tenant admin and the operator screens was a `RouteGuard` reading a permission list the client itself fetched. The server still refused the calls, so this was never an authorization hole — but it put the operator surface one bug (or one stale permission cache) away from a customer's screen. Two deployments put it behind a different hostname, a different image and, in practice, a different access path.

**Blast radius.** The console changes when operations needs something; the dashboard changes when the product does. Merged, every operator tweak re-ships the customer-facing bundle and re-runs its smoke suite, and a broken console build blocks a product release.

**The cost is smaller than ADR-0004 assumed.** The duplication is real — `src/lib`, `src/auth`, `src/components/ui` exist twice — and it is *accepted*, not worked around: see below.

## What is kept from ADR-0004

Everything about how a client talks to the API, which was the good half of that decision and is unchanged:

- **One checked-in contract**, `clients/openapi/v1.json`, exported from the API by `scripts/export-openapi.sh`. Each client generates its own `src/api/schema.d.ts` from that same document with its own `pnpm generate:api`, and calls through `openapi-fetch`. Drift is gated on both sides in CI, and `scripts/check-openapi-drift.sh frontend` now regenerates **both** clients — a stale `schema.d.ts` in either one is drift.
- **Runtime configuration, not build-time**: `/config.json` rendered by the container entrypoint from `APP_*` variables, so one built image promotes across environments.
- **Same-origin transport**: each client is served by its own nginx container that proxies `/api`, because the refresh token is an `HttpOnly; SameSite=Strict` cookie (ADR-0002). Neither client ever calls the API cross-origin.
- **The design system and its tokens**, the TanStack Query conventions, and the small route-mocked Playwright smoke suite per client.

## No shared package

The two trees duplicate `src/lib`, `src/auth`, `src/components/ui` and parts of `src/components/list`. There is deliberately **no** shared workspace package, and neither client is a package of the root workspace:

- Each client's image is built with its own directory as the **Docker build context** (`docker build clients/dashboard`). A shared package outside that context would have to be vendored, or the context widened to the repo root — which would rebuild both images on every backend commit.
- This is a **template**. A team that keeps only the dashboard deletes one directory and nothing else breaks; a shared package would leave them a dangling dependency.
- The duplicated code is the part that changes least, and the two copies are already diverging on purpose: the dashboard's `api-client.ts` has no acting layer and no `X-Console-As-Operator` sentinel at all, and its `token-store` uses its own `boilerplate.dashboard.*` keys.

If duplication starts hurting, the answer is a published internal package, not a workspace link — but do not reach for it before the pain is real.

## Consequences

- **The acting layer lives only in the console**: `src/auth/acting-store.ts`, `src/api/operator.ts`, the `X-Console-As-Operator` sentinel and the acting banner. The dashboard holds exactly one credential, the signed-in user's own, and has no store an acting token could land in. Same-tenant impersonation moved with it — a tenant admin impersonating their own user is done from the console, like every other act-as flow.
- **A non-operator who signs in to the console is told so** (`src/auth/operator-gate.tsx`), rather than being handed a shell whose every panel 403s. The gate is a permission check (`Permissions.Tenants.View` or `Permissions.Platform.Users.Impersonate`), not a tenant-name check.
- **Mailed links point at the dashboard.** `OriginOptions__OriginUrl` — the base for password-reset and email-confirmation links — is the dashboard's origin everywhere (AppHost, compose, Dokploy), because those mails go to a tenant's users. Both origins are in the API's CORS allow-list.
- **Two images, two CI matrix legs, two Dependabot directories, two Traefik routers.** `--frontend` in the template governs both: one without the other is a broken scaffold, and `scripts/template-smoke.sh` asserts each by name.
- ADR-0004 is **superseded**, not deleted: its contract, runtime-config and same-origin decisions are restated above and still hold.
