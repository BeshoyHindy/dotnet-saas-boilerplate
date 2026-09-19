# Frontend — the console (`clients/console`), the OPERATOR tool

Read [`clients.md`](clients.md) first: everything about the API client, runtime env, data
fetching, routing, the design system and testing is shared with the dashboard and is not
repeated here. This file is only what is TRUE OF THE CONSOLE AND NOT OF THE DASHBOARD.

The console is the tool a root operator signs in to (ADR-0008). Dev port **5174**, package
`@boilerplate/console`, image `boilerplate-console`, localStorage prefix `boilerplate.console.*`.

## What it has that the dashboard does not

- **The tenant registry** — `src/pages/tenants/{list,detail}.tsx`, `src/api/tenants.ts`,
  `src/components/tenants/*`: create, renew, adjust validity, activate/deactivate, and edit a
  tenant's branding while acting inside it.
- **The impersonation list** — `src/pages/impersonation/list.tsx`, `src/api/impersonation-grants.ts`:
  the live acting grants and how to revoke one.
- **The acting layer** — below.
- **Identity, audits, health and sessions with an operator's reach**: the same screens the dashboard
  has, kept here because an operator needs them *while acting inside a tenant*. Audits here are the
  cross-tenant view.

## What it deliberately does NOT have

Tenant self-service, which belongs to the app a tenant's users actually use: **My Files**,
**Trash**, and **Settings → Branding** for one's own tenant. Do not add them back; a tenant's
branding is edited from `tenants/detail` while acting, which is the operator's path to it.

## Operators only (`src/auth/operator-gate.tsx`)

`ProtectedRoute` renders `NotAnOperatorView` — "this console is for platform operators" — for any
signed-in user who holds neither `Permissions.Tenants.View` nor
`Permissions.Platform.Users.Impersonate`. A tenant user's credentials are perfectly valid, so the
sign-in succeeds; the honest answer is "wrong app", not a shell of 403ing panels. It waits for
`permissionsHydrated` so a warm reload never flashes it at a real operator, and it stays satisfied
while acting because permissions are hydrated `AS_OPERATOR` (below). `env.dashboardUrl`
(`APP_DASHBOARD_URL`, optional) turns the screen's "Go to the app" into a link.

The gate is a **permission** check, not a tenant-name check: "root" is a seeded identifier, not a
security boundary, and the server gates the same endpoints on the same permission strings.

## Sign-in

Operators live in the root tenant, so the console's login form has **no tenant field** — it always
signs in to `env.defaultTenant` (`APP_DEFAULT_TENANT`, default `root`).

## The acting token (`src/auth/acting-store.ts`, `src/api/operator.ts`, ADR-0002 + issue #9)

Two ways in, one way out, one in-memory credential:

- `useAuth().enterTenant({ tenantId, reason, … })` — the operator token exchange,
  `POST /identity/operator/token-exchange`, root only (`SystemPermissions.Platform.CrossTenantImpersonate`).
  Pass `targetUserId` to act as a specific user rather than the tenant's admin.
- `useAuth().impersonateInOwnTenant({ … })` — `POST /identity/impersonation/start`, **same tenant
  only** since #9; a cross-tenant start is a 403 pointing at the exchange.
- `useAuth().exitTenant()` — `POST /identity/impersonation/end`, which returns **no token**: your own
  session was never taken away.

The acting token lives in **module memory only, never localStorage**: it cannot be refreshed, it should
not survive a browser restart, and a token naming someone else's tenant does not belong in a bucket
every script on the origin can read. A reload therefore drops you back into your own account, by
design. `ActingBanner` (in `AppShell`) is always visible while it is set. Credential screens — 2FA
enroll/verify/disable, change password — are disabled while acting, mirroring the server's
`DenyWhenActing` 403.

Nothing outside the transport ever touches the acting **bearer token**: `AuthContext.acting` is
`ActingSessionView` (`ActingSession` minus `accessToken`) — metadata only, for banners and query keys.
The real credential is read straight off `acting-store` inside `src/lib/api-client.ts`.

### `AS_OPERATOR` — the console-only sentinel

`api-client.ts` exports `AS_OPERATOR_HEADER = "X-Console-As-Operator"` and `AS_OPERATOR`. Adding that
header to a call means "send this with the OPERATOR's own token, not the acting one"; `authFetch`
strips it before the request leaves, so it never reaches the wire. Needed by the two exchange
endpoints (an acting token carries `act_sub` and the server refuses to exchange it again — no
nesting) and by anything that must stay attributed to the operator, such as "my permissions".

**This sentinel does not exist in the dashboard** and must not be copied there: that app has one
credential, so there is nothing to opt out of.

### While acting, a 401 is not a session problem

The acting token is access-only with no refresh cookie, so a 401 on it **drops the acting session**
instead of spending the operator's refresh cookie on a credential nothing can renew. `authFetch` also
captures the acting identity (jti, or null under `AS_OPERATOR`) once per call, before the first send;
if it no longer matches `acting-store` by the time a retry (post-refresh) would go out, the retry is
skipped and a synthetic 401 is returned rather than sending with whatever credential took over in
between.

`AuthProvider` fetches permissions always `AS_OPERATOR` (while acting the endpoint answers for the
subject, and caching a stranger's grants would regate your own chrome — and lock you out of the
operator gate above).

`endSessionLocally()` clears the acting session alongside the token store and the query cache;
`login()` additionally clears `actingStore` up front (before issuing the new token), since it is
establishing a session rather than ending one. An acting session dropped **involuntarily**
(revoked/expired, `acting-store`'s `drop()`) still calls `queryClient.clear()`, not
`invalidateQueries()`: stale data must not be able to render before a refetch replaces it.
