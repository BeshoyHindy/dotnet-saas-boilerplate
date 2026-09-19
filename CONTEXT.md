# Boilerplate

A multi-tenant SaaS product: one modular .NET monolith, one React console, one deployment shape.
This is the glossary — the words this repository uses and the words it refuses. Decisions behind them
live in `docs/adr/`.

## Language

### Tenancy

**Tenant**:
One customer of the product, and the isolation boundary every row, cache key, storage key, job and
event belongs to. A request names its tenant only through the signed `tenant` claim (ADR-0002).
_Avoid_: organisation, account, workspace, company, customer (a customer is a commercial relation,
not a boundary).

**Root tenant**:
The single tenant that owns the platform itself, `root`. It is a Tenant in every mechanical respect;
what makes it root is that only its members may hold root permissions.
_Avoid_: host tenant, system tenant, superadmin tenant, master.

**Operator**:
A user of the root tenant acting on the platform rather than in a product. An operator is still
pinned to one tenant per token; crossing into another tenant is a Tenant exchange.
_Avoid_: superadmin, sysadmin, host user, platform admin.

**Tenant exchange**:
The act of an operator obtaining a short-lived, access-only token whose tenant is another tenant and
whose subject is a real user there. The only supported way to cross a tenant boundary.
_Avoid_: tenant switch, cross-tenant login, tenant override, impersonating a tenant (a tenant is not
a person).

**Acting**:
The state of holding a token minted for another identity — by Tenant exchange, or by same-tenant
impersonation. Marked by the `act_sub` / `act_tenant` claims, which name the real actor. An acting
token is access-only, held in memory only, and may not touch the subject's credentials.
_Avoid_: impersonating (use it only for the same-tenant case), sudo, masquerading, "logged in as".

**Global entity**:
A row that genuinely belongs to no tenant and is therefore exempt from the tenant filter, declared by
`IGlobalEntity`. Absence of the marker means isolated; there is no third state.
_Avoid_: shared entity, system entity, untenanted.

### Identity and authorisation

**Session**:
One device's long-lived login, represented by `UserSession`. The session *is* the refresh token: the
row is the credential's only record, and its id is the `sid` claim carried by every access token
issued from it.
_Avoid_: refresh token record, login, device (a user may have several sessions on one device).

**Permission**:
One named capability in the registry (`IPermissionRegistry`), the only unit an endpoint may demand.
Checks are all-of: every permission an endpoint lists must be held. A permission flagged root is
grantable only inside the root tenant.
_Avoid_: role (a role is a bundle of permissions), scope, claim, policy, `PermissionConstants` (the
static class is gone).

**Authorization intent**:
The single declaration every endpoint must carry about who may call it — a permission set,
authenticated-only, or anonymous. There is no default: an endpoint that declares nothing is denied
for everyone and fails the build.
_Avoid_: auth attribute, endpoint policy, "unsecured endpoint" (there is no such endpoint).

### Work in the background

**Tenant job**:
Background work that belongs to exactly one tenant. It carries only the tenant Id and runs inside
that tenant's scope; a tenant job with no tenant fails rather than running tenant-less.
_Avoid_: scoped job, user job, per-tenant task.

**System job**:
Background work that genuinely belongs to no tenant, declared by `[SystemJob]` — maintenance,
provisioning, and the sweeps that fan out across tenants. Every recurring job is a system job,
because the scheduler triggers it with no tenant in scope.
_Avoid_: global job, platform job, cron job, background task.

**Tenant sweep**:
A system job that visits every tenant one at a time through `ITenantScope.RunForEachTenantAsync`,
catching per tenant so one failure does not end the sweep. The shape that replaces a cross-tenant
query.
_Avoid_: fan-out (too generic), batch job, cross-tenant job.

**Integration event**:
A fact one module publishes for other modules to consume, asynchronously and durably. It is published
under a tenant, like everything else.
_Avoid_: message, notification (Notifications is a module), domain event (that is the in-process,
pre-commit tier).

**Global integration event**:
An integration event that genuinely belongs to no tenant, declared by `IGlobalIntegrationEvent` — the
event-side counterpart of a system job. A blank tenant without the declaration is a bug, not a
platform-wide event.
_Avoid_: system event, broadcast event, untenanted event.

### Shape of the codebase

**Module**:
A bounded context: one runtime project plus one `.Contracts` project that is its entire public
surface. There are five and only five — Identity, Multitenancy, Auditing, Files, Notifications
(ADR-0003) — and a module never references another module's runtime.
_Avoid_: service, package, feature area, subsystem.

**Reachability rule**:
The rule that decides what else survives: a building block, package or feature stays only if one of
the five modules or a host references it. Nothing is kept because it might be useful.
_Avoid_: dead code policy, pruning, tree-shaking.

**Building block**:
Shared framework code under `src/BuildingBlocks/`, consumed by every module and owned by no module.
Changing one has the blast radius of the whole application.
_Avoid_: common, shared library, infrastructure, core (Core is one building block among several).

**Dashboard**:
The React application a tenant's own users sign in to (`clients/dashboard`) — the product (ADR-0008).
It holds one credential, the signed-in user's own, and has no platform surface.
_Avoid_: portal, frontend, the app, tenant console.

**Console**:
The React application root operators sign in to (`clients/console`) — the operator tool (ADR-0008):
the tenant registry, the acting layer, and the screens an operator needs while acting. A tenant user
who signs in there is told it is not their app.
_Avoid_: admin, superadmin app, backoffice, the dashboard (that is the other client).

**The clients**:
The two of them together (`clients/dashboard` and `clients/console`). Between 2024's ADR-0004 and
ADR-0008 there was only one, which is why older comments say "the console" where they mean "a client".
_Avoid_: the frontend, the SPA (there are two).

**Contract**:
The checked-in OpenAPI document `clients/openapi/v1.json` — the one agreed description of the API.
The console's types are generated from it and nothing hand-writes an API type.
_Avoid_: schema, spec, swagger, API docs.

**Drift gate**:
The CI check that re-derives both sides of the Contract — re-exporting the document from the API and
regenerating the console's types — and fails when either differs from what is committed.
_Avoid_: codegen check, sync check, lint.

### Running and shipping it

**Migrator**:
The one-shot host that applies migrations and seeds, run to completion before the API starts. The API
never migrates.
_Avoid_: migration runner, init container, bootstrapper.

**Demo account**:
One of the accounts the Migrator creates under `--demo`, in the `acme` and `globex` demo tenants, so
a fresh stack can be signed into. They all share one configured password, they exist only outside
Production, and they are the first thing a real product deletes. The root tenant's operator is not
one of them.
_Avoid_: test user, sample data, seed user (seeding also creates the root tenant and every tenant
admin, which are not demo accounts), fixture.

**Stack**:
One deployable compose unit. There are two: the data-services stack (database, cache, object storage)
and the app stack (migrator, API, console). Staging and production run the same two stacks with
different values (ADR-0005).
_Avoid_: environment (an environment is staging or production), deployment, service, cluster.

**Public file URL**:
A link to a file that anyone holding it can read. For Files-module assets it is minted per read,
presigned and short-lived, so a copied link dies with its signature. Nothing persists one.
_Avoid_: permanent link, CDN URL, share link.

**Private file URL**:
A link to a file that only the requester may read, presigned for them. The default: the Files key
space is never anonymously readable.
_Avoid_: signed URL (both are signed), secure link.

**`uploads/` prefix**:
The one storage key space published for anonymous read — avatars and tenant theme assets. A durable,
unsigned URL is valid only here; anything stored on an entity column belongs here.
_Avoid_: public bucket, static assets, CDN folder.

## Ambiguous words to avoid outright

- **admin** — say *operator* (root tenant) or *tenant admin* (the role inside a tenant).
- **client** — name the one you mean: *dashboard* (the tenant app) or *console* (the operator tool);
  *the clients* is fine for both together. *API client* is the generated code, never an app.
- **user** — fine for a person; never for a Tenant.
- **global** — reserved for the two declared exemptions (`IGlobalEntity`,
  `IGlobalIntegrationEvent`). Do not use it to mean "shared" or "platform-wide" elsewhere.
- **switch tenants** — there is no switch; there is a Tenant exchange.
- **impersonate** — same-tenant only. Cross-tenant is a Tenant exchange, and both put you in the
  Acting state.
