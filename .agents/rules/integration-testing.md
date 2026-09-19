# Integration testing

`src/Tests/Integration.Tests/` + `Integration.Middleware.Tests/`. Read before writing tests that touch the DB/HTTP pipeline. See `testing.md` for unit conventions.

## Harness

`WebApplicationFactory` over **real** infra via Testcontainers — PostgreSQL + Redis + MinIO. **Docker must be running**; if it isn't, tests fail fast with `DockerUnavailableException` (environmental, not a regression — run the unit projects instead).

`AppWebApplicationFactory` (`Integration.Tests/Infrastructure/`) boots the containers, overlays in-memory config, swaps `IMailService` → `NoOpMailService`, and rewires storage to MinIO.

## Must-know gotchas

- **Tenant context is AsyncLocal — set it inline.** Set the Finbuckle tenant context **in the same method** as the `UserManager`/`DbContext` call. Setting it in an awaited helper loses it across the async boundary → NRE in the tenant query filter.
- **Storage is wired eagerly.** `AddHeroStorage` reads `Storage:Provider` before the test config overlay, so it picks `LocalStorageService`. The factory **removes the `IStorageService`/`LocalStorageService`/`S3StorageService` descriptors post-registration and re-registers the S3 stack** at MinIO. Follow that when a test needs real object storage. (See `storage.md`.)
- **Rate limiting is read eagerly** — `Integration.Middleware.Tests` sets `RateLimitingOptions:Enabled` via env var **before** host build, since flipping it after has no effect.

## The cross-tenant sweep — read this before adding an endpoint

`Integration.Tests/Tests/Multitenancy/TenantEndpointSweepTests.cs` enumerates **every route the host publishes** from `EndpointDataSource` and, for each one that takes a resource id, calls it with tenant A's token and tenant B's id. The required answer is **404** — not 200 (the leak), not 403 (which answers the existence question anyway and puts isolation behind a permission), not 400 (a validator fired before the lookup, so the probe proved nothing).

Because it enumerates, **your new endpoint is swept the day you map it**. Two things are asked of you:

1. **A seedable resource.** If the route's id has no entry in `TenantSweepRegistry.ByRouteKey`, the coverage test fails and names the key to add. Keys are `"{preceding literal segment}/{parameter name}"` — `files/{id}` → `files/id` — so a new route under an existing noun (`GET files/{id}/thumbnail`) costs nothing. A new noun needs a `ResourceKind` and a seeder in `TenantSweepSeeder`.
2. **A body sample**, if a validator would 400 before the handler looks the row up: add one to `TenantSweepBodies.ByEndpoint`, keyed `"METHOD template"`. The factory gets the substituted route values *and* the calling tenant, so a body can name the caller's own user.

**Opting out is explicit and costs a sentence:** `.ExemptFromTenantSweep("why this id is not a tenant-scoped resource")` at the mapping site (`Boilerplate.BuildingBlocks.Shared.Multitenancy`). The reason is mandatory and the sweep prints the exempt list on every run. It is not a way to silence a leak — an endpoint that answers 200 or 403 for another tenant's id is a bug in the endpoint.

The sweep also runs a **positive control** (the same request with the caller's own id must not 404), a **root-token pass** (root has no override — ADR-0002 — so it must 404 too, except for the declared platform-wide kinds), and a **list pass**: every seeded row carries a per-tenant marker, and no collection endpoint called with A's token may contain it. The list pass needs no registration at all.

### The sweep covers route ids only

It substitutes tenant B's id into **route parameters**. An id that arrives in the **body** or the **query string** has no parameter to substitute, so the endpoint is swept as if it addressed nothing — the probe runs, returns 200, and proves nothing. Body ids reach the same handlers and the same queries, and they are the ones a client can set freely: a role id inside `POST identity/roles`, role ids inside `POST identity/users/{id}/roles`, user ids inside `POST identity/groups/{groupId}/members`, an `ownerId` inside `POST files/upload-url`.

**So an endpoint that takes a row id in the body or the query needs a hand-written cross-tenant test.** `Integration.Tests/Tests/Multitenancy/CrossTenantBodyIdTests.cs` is where they live; it shares `TenantSweepFixture`, so the two provisioned tenants and their seeded rows cost nothing extra. Assert both halves: nothing of tenant B changed, and nothing of B was granted to A. Pick the status from what the handler actually does (404 when it resolves the id, 403 when it compares it to the caller) and say why in the test.

## Coverage

```bash
dotnet test --collect "XPlat Code Coverage" --settings coverage.runsettings
```
Cobertura; includes `[Boilerplate.Modules.*]` + `[Boilerplate.BuildingBlocks.*]`; excludes tests, the Migrations project, and `*HostedService`.
