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
- **End-impersonation is access-only.** It runs in the impersonated tenant's context and so cannot write a session row in the actor's tenant; operator token exchange (#9) is where crossing a tenant boundary gets its mechanism.

## Tests

`Identity.Tests` is the largest unit suite. When asserting a forwarded `CancellationToken`, assert the specific token (see `testing.md`).
