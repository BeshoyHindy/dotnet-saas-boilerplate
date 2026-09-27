---
status: accepted
---
# The Job monitor opens from the console through a second, route-scoped authentication scheme

The API is bearer-only (ADR-0002): every request carries `Authorization: Bearer`, and no cookie
authenticates anything. Hangfire's dashboard — renamed the **Job monitor** here, because
*Dashboard* is the tenant client (ADR-0008) — is mounted at `/jobs` behind the root-only
`Permissions.Hangfire.View`. A browser navigating to it cannot attach a bearer header, and neither
can the pages' own stylesheets, scripts and polling requests, so in practice no operator could open
it. It is the only view of failed and retrying jobs the template ships, so it stays, in every
environment including Production. Decided in issue #93, built in #102.

## Decision

**The console hands out a short-lived, route-scoped cookie, and a second authentication scheme
accepts it on the Job monitor route only.**

- The console shows a *Jobs* item to holders of `Hangfire.View`. Opening the Job monitor calls
  `POST /api/v1/identity/operator/job-monitor-access` with the operator's own bearer token, then opens
  `/jobs` in a new tab.
- That endpoint requires `Hangfire.View`, is root-only, is refused to acting tokens (the existing
  `DenyWhenActing` filter), and records every issue as a security audit event (`TokenIssued`,
  client `job-monitor`).
- It sets `__Secure-job_monitor`: `HttpOnly; Secure; SameSite=Strict; Path=/jobs`, **no `Domain`**, a
  fixed 15-minute life with no sliding — the console issues a fresh one each time. The value is a
  token signed with the API's key, carrying the operator's identity claims and `sid`, minted for an
  audience of its own (`{Audience}/job-monitor`). The response body never contains it.
- The console's nginx forwards `/jobs` to the API exactly as it forwards `/api`, so the cookie
  belongs to the console's origin and nothing else.
- The second scheme (`JobMonitorCookie`, a JWT handler reading the cookie) is chosen by a forwarding
  default scheme **only** when the matched endpoint carries `JobMonitorEndpointMetadata`, the request
  has no `Authorization` header, and the cookie is present. Everything else is authenticated by the
  bearer scheme exactly as before, and the Job monitor keeps accepting bearer tokens for API clients.
- On every Job monitor request the cookie's `sid` is checked against the session, as bearer tokens
  are (#118): logging out or revoking the session ends access.
- `Hangfire.View` opens the Job monitor **read-only**. A new root permission, `Hangfire.Manage`,
  makes it writable (retry, delete, trigger) — it drives Hangfire's read-only switch.
- CSRF: `SameSite=Strict`, plus Hangfire's own antiforgery check on every write, now active because
  the API registers `IAntiforgery` (its token cookie is also scoped to `/jobs`).

A scaffold made with `--frontend false` has no console and therefore no cookie: its Job monitor stays
bearer-only. That is documented, not worked around.

## Why a forwarding default scheme

Tenant resolution and the token-without-tenant guard read `HttpContext.User` in module middleware,
between `UseAuthentication` and `UseAuthorization`. A scheme named only on the endpoint is
authenticated by the authorization middleware — after tenant resolution has already run with an
anonymous user — so the permission check would find no tenant. The cookie therefore has to be
authenticated in `UseAuthentication`, which means making the default authenticate scheme a selector.
The selector keys on endpoint metadata, not on a path prefix, because `HangfireOptions.Route` is
configurable. The permission policy names the same selector, for the same reason.

## Why the two credentials cannot stand in for each other

A Job monitor token sent as a bearer header is refused everywhere: the bearer scheme accepts only the
API audience. An access token placed in the cookie is refused on the Job monitor: the cookie scheme
accepts only the Job monitor audience. A Job monitor cookie sent to any other route is never read,
because the selector only consults it on the Job monitor endpoint — and the browser only sends it
under `/jobs` in the first place.

## Rejected

- **A signed link** (`/jobs?token=…`). The token leaks through access logs, proxies and browser
  history, and it still has to become a cookie, because the page's assets and polling cannot carry
  the query string.
- **API-client-only access** — document "send a bearer header" and stop. Correct, and unusable in
  practice: nobody operates a dashboard through curl.
- **Dropping the dashboard from Production.** Loses the only view of failed jobs the template ships,
  for no security gain over a root-only, session-bound, audited, 15-minute cookie.
- **ASP.NET Core cookie authentication.** Its tickets are Data Protection payloads, whose keys are
  only shared across restarts and replicas when Redis caching is configured. A signed token with its
  own audience needs nothing the bearer scheme does not already have.

## Consequences

- The API is no longer strictly bearer-only. It is bearer-only everywhere except one route, and that
  exception is a named scheme, a marker on one endpoint, and a selector — all in the Identity module
  and `BuildingBlocks/Jobs`. Removing the Job monitor removes the exception.
- Revocation of Job monitor access has the same bound as revocation of a bearer token: immediate on
  the instance that revoked, within `SessionLiveness.CacheDuration` elsewhere.
- `Hangfire.Manage` is a new root permission. The root tenant's Admin role receives it from the
  registry like every other root permission; any other operator role must be granted it explicitly.
