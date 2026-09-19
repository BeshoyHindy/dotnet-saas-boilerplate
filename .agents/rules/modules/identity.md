# Module: Identity

Auth (JWT + ASP.NET Identity), users, roles, permissions, sessions, impersonation, 2FA.

## Service shape

`IUserService` is a **facade** that delegates to focused single-responsibility services — change behavior in the specific service, not the facade:

| Interface | Concern |
|---|---|
| `IUserRegistrationService` | register, external-principal create, email/phone confirm |
| `IUserProfileService` | get/list/count, update profile, image, existence checks |
| `IUserStatusService` | activate/deactivate (`DeleteAsync` == deactivate), audited toggles |
| `IUserRoleService` | role assignment, admin-role guards |
| `IUserPasswordService` | forgot/reset/change password, history + expiry |
| `IUserPermissionService` | effective permissions, cache invalidation |

`ChangePassword`/`Update`/`Delete` etc. flow facade → service → EF/UserManager. `CancellationToken` is `= default` on these interfaces and propagated into EF sinks (note: `UserManager`/`RoleManager` have no CT overloads, so private helpers that only call them don't take one).

## Permission gating footgun

`RequiredPermissionAttribute` implements `Boilerplate.BuildingBlocks.Shared.Identity.Authorization.IRequiredPermissionMetadata`. **Never let a second/duplicate `IRequiredPermissionMetadata` appear** — it silently disables **all** `.RequirePermission()` gates across the app. Permission constants live in `Shared/Identity/*Permissions.cs`.

## Hosted services (background)

- `RolePermissionSyncHostedService` — best-effort sync of the permission catalog; loops, catches `Exception` *with* an `OperationCanceledException` filter, logs and continues.
- `SessionCleanupHostedService` — hourly expired-session purge; OCE handled by a preceding catch.

These are the model for background loops: stay alive, log with context, never swallow cancellation. See `api-conventions.md`.

## Tokens / sessions

Login `POST /api/v1/tenants/{tenant}/auth/token` (header `X-Client-App` enforces the operator/tenant app boundary). Refresh `POST /api/v1/tenants/{tenant}/auth/refresh` cross-checks subject. Both live in the anonymous auth group alongside forgot-password, reset-password, confirm-email and register — the only endpoints that take the tenant from the route (ADR-0002). Admin can't demote/deactivate the last admin or the root-tenant seed admin (guards in `UserRoleService`/`UserStatusService`).

**One session store, and it *is* the refresh token** (`UserSession`, one tenant-isolated row per device):

- `ITokenService` mints **access tokens only**. `ISessionService.CreateSessionAsync` mints the refresh token, so login creates the session *before* the access token — its id becomes the `sid` claim. **A session-creation failure fails the login** (no try/catch): a login with no session row can neither refresh nor be revoked.
- The token is `"{tenantId}.{32 CSPRNG bytes, base64url}"` (`RefreshTokenValue`), stored only as SHA-256. The prefix is routing metadata, never a credential — it must equal the resolved tenant, and the hash lookup runs inside the tenant query filter. **No `IgnoreQueryFilters()` anywhere on the token path.**
- `RotateRefreshTokenAsync` is one compare-and-set `ExecuteUpdate`, so N concurrent refreshes yield exactly one winner (losers get `Superseded`). A `PreviousTokenHash` hit is reuse → the session is revoked. A `SecurityStamp` change (password reset, credential change) kills the session. `sid` survives rotation.
- Browsers also get the token as `HttpOnly; Secure; SameSite=Strict` cookie (`RefreshTokenCookie`) pinned by `Path` to the tenant's refresh route; the refresh endpoint falls back to it when the body omits the token. CORS still allows no credentials (#13), so the cookie is same-site only and body delivery remains the client path.
- **Every response that is not a successful rotation clears the cookie**, via `DeleteWhenResponseStarts` — an `OnStarting` callback, because `UseExceptionHandler` wipes headers before re-running the pipeline and an eager `Set-Cookie` would vanish from exactly the 401s that need it (same reason as the security headers; see `security.md`). `Delete` must keep every attribute identical to `Append`, `Path` above all: a browser matches a deletion by name + Path, so a mismatch silently leaves the credential in place.
- **End-impersonation returns no token.** The actor's own session is never taken away while they act as someone else, so End just marks the grant ended (which kills the acting token on its next request). The old access-only token it used to mint had no refresh counterpart — a credential nothing could renew.

## Acting as someone else (impersonation + operator token exchange)

`IImpersonationTokenIssuer` is **the** place a token is minted for another identity. Two surfaces call it and they share everything downstream — one `ImpersonationGrant` table, one jti revocation list, one lifetime ceiling (`OperatorExchange:MaxMinutes`, clamped server-side), one audit record:

- `POST /identity/impersonation/start` — **same tenant only**. A cross-tenant caller (root included) gets 403 pointing at the exchange.
- `POST /identity/operator/token-exchange` — **root only** (`SystemPermissions.Platform.CrossTenantImpersonate`, the catalog's "Cross-Tenant Impersonate"), plus a root-tenant check in the handler. The subject is a real user of the target tenant (`targetUserId`, else the tenant record's `AdminEmail`), so the normal permission pipeline applies unchanged. Reason required; unknown tenant 404, deactivated tenant 403, unknown user 404, deactivated user 409. No refresh token, no session row, no cookie.

Neither accepts a caller that already carries `act_sub` (no nesting). The audit row lands in the **ambient** tenant — the caller's own — so an operator finds their crossings in root. `BuildClaimsForUserAsync`/`FindTenantUserAsync` read the target user with `IgnoreQueryFilters` from the ambient database, so a tenant with a dedicated connection string cannot be entered until the tenant-scope helper lands.

`RevokeImpersonationGrant` takes effect immediately on the instance that handled the revoke, and within the `ImpersonationGrantService` local cache's expiration (up to 1 minute, see `Services/ImpersonationGrantService.cs`) on any other instance — not the flat "~1 second" the endpoint used to claim.

**An acting token (`act_sub` present) must never be able to change or reveal the subject's own credentials.** `.DenyWhenActing()` (`BuildingBlocks/Shared/Identity/Authorization/DenyWhenActingEndpointFilter.cs`) throws `ForbiddenException` (403) when the caller's token carries `act_sub`; it is applied to 2FA enroll/verify/disable and change-password. It is deliberately **not** applied to session revocation, profile name/image updates, or the impersonation end/revoke endpoints — those either don't touch credentials or are how an actor cleans up after themself.

### Logout

`POST /api/v1/tenants/{tenant}/auth/logout` — **`AllowAnonymous` on purpose**: it has to work when the access token is already gone, which is the state a signing-out browser is in. It revokes the session named by the caller's `sid` claim, or failing that the session the supplied refresh token belongs to (body or cookie, current *or* previous hash), and always clears the cookie. Always 204 — a different answer for a live token than a dead one would be an oracle. Both clients call it best-effort from `logout()` before clearing local state.

Clearing localStorage alone is **not** a logout: the SPA cannot delete an HttpOnly cookie and `/auth/refresh` accepts that cookie on its own. Note the cookie's `Path` is the refresh route, so a browser does not send it to `/logout` — identification comes from the `sid` claim or the body token, while the deletion works regardless (a `Set-Cookie` may name any `Path`).

### `sid` is issued, not enforced

`sid` names the session row, but **nothing validates it per request** — there is no session lookup in the auth pipeline, by design (it would put a database read on every call). So revoking a session stops *refresh* immediately and stops API access only once the current access token expires (`JwtOptions.AccessTokenMinutes`, default 30). Revocation is eventually consistent for API access, and that window is the deliberate price of stateless JWT validation. Shorten `AccessTokenMinutes` if an application needs a tighter bound; don't add a per-request `sid` check without deciding how to pay for it.

## Tests

`Identity.Tests` is the largest unit suite. When asserting a forwarded `CancellationToken`, assert the specific token (see `testing.md`).
