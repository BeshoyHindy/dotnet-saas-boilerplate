---
status: accepted
---
# The tenant is resolved only from the signed token

Every access token carries exactly one `tenant` claim (the immutable tenant Id, not the renameable identifier), and on an authenticated request that claim is the *only* input to tenant resolution. The `tenant` header strategy, the `?tenant=` query strategy and any host strategy are deleted, not disabled. `UseAuthentication()` runs before tenant resolution; an authenticated principal without a `tenant` claim is rejected with 401.

The upstream starter kit resolved the tenant from a caller-supplied header and relied on tenant-scoped user rows to make a foreign header harmless, then added post-auth middleware to compensate. That is isolation by coincidence of several layers. We want it by construction: the caller cannot name a tenant, so there is nothing to validate.

## The rules

- **Login and other anonymous, tenant-scoped endpoints** (login, forgot/reset password, confirm email) carry the tenant in the route: `/api/v1/tenants/{tenant}/auth/...`. Resolution order is: authenticated → claim; anonymous → `{tenant}` route value; otherwise no tenant. An authenticated request never reads the route value.
- **Refresh tokens** are opaque `"{tenantId}.{32 CSPRNG bytes}"`, stored only as a SHA-256 hash on a tenant-isolated `UserSession` row (one row per device). The prefix routes the anonymous refresh call to a tenant; the hash lookup then runs inside that tenant's query filter, so a token presented under another tenant's prefix matches nothing. Rotation is a single compare-and-set `ExecuteUpdate`; the previous hash is kept, and presenting it revokes the session (reuse detection). Browsers receive the refresh token as an `HttpOnly; Secure; SameSite=Strict` cookie scoped to the refresh path.
- **Root operators cross tenants by exchanging tokens, never by header.** A root principal with the right permission calls an operator endpoint and receives a short-lived, access-only token whose `tenant` is the target and whose `act_sub`/`act_tenant` claims record the operator. The exchange is audited. This keeps the invariant "one token, one tenant" without exceptions, and unifies with impersonation.
- **Jobs and events** capture the tenant Id from the ambient tenant context at enqueue/publish time (not from `HttpContext`), and only the Id travels — a job argument and an outbox row never carry a tenant record. On execution the tenant scope is opened *before* the DI scope: the record is re-read from the store (cache-first) so an unknown or deactivated tenant fails the work closed, and so the tenant is ambient before any tenant-filtered `DbContext` in that scope is constructed. Work that is genuinely tenant-less must be declared as a system job; an undeclared job with no tenant fails.
- **Persistence** is **one shared database for every tenant**. Isolation is the `TenantId` column and the default-on query filter, never a separate database: every entity is tenant-filtered unless it implements `IGlobalEntity`. `IgnoreQueryFilters()` is allowed only in an allow-list enforced by an architecture test.

## Considered options

- *Keep the header, add a claim-equals-header binding check* (what the reference fork did): still leaves a caller-controlled tenant input and a root header override; rejected.
- *Tenant in the login body*: requires reading the body inside resolution middleware; the route value is available for free after routing.
- *Require the expired access token on refresh to learn the tenant*: couples refresh to a second credential and breaks cookie-only browser refresh.

## Consequences

- Token-only transports (SignalR, SSE) work unchanged, because they already carry the token.
- Tests are part of the decision: an architecture test bans header/query strategies and unlisted `IgnoreQueryFilters()`; an integration suite on real PostgreSQL proves a tenant-A token gets 404 for every tenant-B resource id on every endpoint, that a forged `tenant` header is ignored, that a refresh token fails under another tenant's prefix, and that jobs and event handlers run under the enqueuing tenant.

## Amendment — 2026-09-20: per-tenant databases cut

Tenant connection strings — a dedicated database per tenant — are removed from the template (#75). `AppTenantInfo.ConnectionString`, the `CreateTenant` connection-string field, the connection-string validator, the tenant branch in `BaseDbContext` and `IdentityDbContext`, the outbox drain-target machinery, the per-tenant migrate loop and the tenant-migrations health check and query are all deleted, not disabled.

The tier was real — some SaaS buyers ask for it — but it was never finished, and the price was paid on every path: an outbox dispatcher that had to enumerate databases, a migrator and a health check — never tagged for readiness — that fanned out N times to ask one question, and two known gaps (operator token exchange could not enter a dedicated-database tenant; the data sweeps did not visit one). Isolation by column and query filter is the invariant this ADR was written to defend, and it needs none of that.

What survives, and why, since the reasons in the bullets above changed: the tenant record is still re-read before the DI scope on every job and dispatched event — now so that an unknown or deactivated tenant fails the work closed, and so the tenant is ambient before a tenant-filtered `DbContext` is constructed. `ITenantScope` remains the only way to enter a tenant outside a request. A product that needs a dedicated-database tier builds it deliberately, on top of this.
