# Upstream delta since the snapshot

Research for [#89](https://github.com/BeshoyHindy/dotnet-saas-boilerplate/issues/89), part of the map [#87](https://github.com/BeshoyHindy/dotnet-saas-boilerplate/issues/87).

- **Upstream:** [fullstackhero/dotnet-starter-kit](https://github.com/fullstackhero/dotnet-starter-kit), compared `3f2959e...main` (17 commits, all dated 2026-09-25/26), plus every open PR as of 2026-09-26. The ticket counts 8 open PRs; there are 9, because #1348 and #1356 are older ones.
- **This repo:** `develop` at `4e0c1da`.
- **Method:** each upstream commit's patch (`gh api repos/fullstackhero/dotnet-starter-kit/commits/<sha>`) and each PR diff (`gh pr diff`) was read, then checked against the code here. Every "present here" or "already have" below cites the file and line it rests on. Where a claim comes only from upstream's own commit message or tests and was not reproduced here, the row says so.
- **Rule applied (map #87 Notes):** a recorded decision (an ADR in `docs/adr/` or a closed ticket of [map #1](https://github.com/BeshoyHindy/dotnet-saas-boilerplate/issues/1)) wins over upstream by default. A row says a decision is challenged only when the upstream change exposes a real bug that the decision missed.

Classifications: **already have** · **conflicts** (with a recorded decision) · **port** (worth porting) · **skip**.
Sizes: XS < 20 lines · S < 150 · M < 500 · L beyond that (production code plus tests, rough).

## Summary

| Upstream | Change | Classification | Size | One-line reason |
|---|---|---|---|---|
| `ee6a974` | honour `X-Forwarded-*` for the real client IP | already have | – | `ProxyOptions` already does this, fails closed, and never honours `X-Forwarded-Host` (#13) |
| `4598759` | reject `ForwardLimit < 1` at startup | already have | – | `[Range(1, 16)]` plus `ValidateOnStart` |
| `6221ffe` | resolve the front-end origin per request | conflicts (#46) | S if adapted | the decision holds. It leaves a UX gap: no mail link reaches the console's auth pages |
| `7e34c31` | MinIO → RustFS for local/test | already have (differently) | – | Chainguard MinIO pinned by digest, in all stacks including production (`928decc`) |
| `bf86648` (a) | replay body/status, store-before-flush, scoping, fail-open | already have | – | rewritten in #82/#84/#85 |
| `bf86648` (b) | Redis `SET NX` cross-instance reservation | conflicts (#82) | – | single-process window documented on purpose; the shipped deploy runs one API replica |
| `bf86648` (c) | detach the client-abort token from the handler and the capture | **port** | S | a gap #82 did not consider: a disconnect can cancel a committed handler before anything is stored |
| `921be0e` | mask sensitive values in entity diffs; `IAuditExempt` | **port (P1)** | S | `PasswordHash`, `SecurityStamp` and token hashes are copied verbatim into `AuditRecords` today |
| `6f8d38d` + `27f23d6` | real HTML mail with a text alternative; one shell, one encoder | **port** | M | welcome mail interpolates `FirstName` unencoded into HTML; reset mail is plain text sent as HTML |
| `dcb3525` | CORS: allow PATCH and client headers | **port (PATCH only)** | XS | two PATCH endpoints, and PATCH is missing from the restricted policy. Only matters cross-origin |
| `d02a50b` | `AmbientDbTransactionRegistry` never receives events | **port** | S | the bug is present verbatim. It stays hidden on PostgreSQL |
| `eb01b14` | ETag / If-Match on profile updates | **port** | M | lost update on `PUT /identity/profile`; a concurrency failure surfaces as 500 |
| `401fc1c` | stop the idle aurora repaint | **port (console only)** | XS | the console still animates `background-position` forever; the dashboard does not |
| `c6ccb81` | Postgres 18 volume path | already have | – | both compose stacks mount `/var/lib/postgresql` |
| `a0813e6` | Aspire 13.5.4 + all NuGet to latest | see [versions](#version-bumps-and-breaking-changes) | M | feeds the dependency-upgrade ticket |
| `d7cb557` | SourceLink 10.0.401 (GHSA-23fw-v26w-5fgq) | already have | – | on 10.0.112, and the audit is clean |
| `69dba68` | Testcontainers 4.14.0 (SSH.NET advisory) | already have (differently) | – | direct `SSH.NET 2026.0.0` pin; Testcontainers still 4.11.0 |
| `894f1a2` | frontend Dependabot patches | already have | – | every bumped package is already at or above the target in both clients |
| PR #1376 | database-backed Data Protection keys | skip | – | keys persist to Valkey with AOF and no eviction; the PR depends on #1372/#1374 (out of scope) |
| PR #1374 | SQL Server provider | skip | – | out of scope: PostgreSQL only (map #87, #6) |
| PR #1372 | CLI packages, `.agents` scaffolding, fork-local templates | skip | – | out of scope: scaffolding is `dotnet new saas` (ADR-0001) |
| PRs #1381–#1384 | localization series | shape only | L | see [localization](#localization-prs-13811384); adoption is a later decision |
| PR #1356 | activity-feed hydration | skip | – | `clients/dashboard/src/pages/activity.tsx` does not exist here |
| PR #1348 | Catalog SSE broadcasting | skip | – | Catalog and SSE were removed (#4, #6) |

Counts: **17 commits:** 8 already have (one of them split with a conflicting part and a port) · 1 conflicts outright · 7 port · 1 version bundle. **9 PRs:** 5 skip · 4 localization (shape only).

## Commits

### `ee6a974`: honour `X-Forwarded-*` so the real client IP reaches the pipeline

**Already have.** The ticket asks whether this repo gets the real client IP behind a trusted proxy today. It does:

- `src/BuildingBlocks/Web/Security/ProxyOptions.cs` together with `ConfigureForwardedHeaders.cs:29-66` applies `XForwardedFor | XForwardedProto` only (line 29; `X-Forwarded-Host` is excluded on purpose, per the #13 decision). It clears and rebuilds the trust lists (`:33-34`) from `KnownProxies` / `KnownNetworks`.
- `ProxyOptionsValidator.cs:36-43` fails the boot when proxy support is enabled but nothing is trusted, so the configuration cannot silently trust everyone.
- `src/Host/Boilerplate.Api/appsettings.Production.json:53-58` sets `ProxyOptions.Enabled: true` and `TrustAnyProxy: false`. `deploy/dokploy/app.compose.yml:83` names the proxy network (`ProxyOptions__KnownNetworks__0: ${PROXY_KNOWN_NETWORK}`), and `deploy/dokploy/tests/compose-contract.test.sh:236-244` pins it.
- `src/BuildingBlocks/Web/Extensions.cs:161-163` runs `UseForwardedHeaders()` first in the pipeline. The rate limiter (`RateLimiting/Extensions.cs:106,126`) and `RequestContextService.cs:27` (audit and session IP) then read the rewritten `RemoteIpAddress`.
- Tests: `Integration.Middleware.Tests/Tests/ForwardedHeadersTests.cs` and `Framework.Tests/Web/HardeningOptionsTests.cs:64-147`.

Upstream's version is looser: with nothing configured it trusts loopback. This repo fails closed.

One optional hardening nit came out of upstream's last fix-up in that commit. `ConfigureForwardedHeaders.cs:19-22` returns early when proxy support is disabled and leaves the lists alone. If someone set `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true`, ASP.NET Core's own `ForwardedHeadersOptionsSetup` would empty both lists, and an empty list means "trust any peer". Nothing in this repo sets that variable (grepped: no hits), so this is not a live bug. Clearing the lists in the disabled branch as well would close it. Size XS.

### `4598759`: reject a trusted-proxy `ForwardLimit` below 1 at startup

**Already have.** `ProxyOptions.cs:42-43` has `[Range(1, 16)] ForwardLimit`, bound with `.ValidateDataAnnotations().ValidateOnStart()` (`Web/Extensions.cs:137-140`).

### `6221ffe`: resolve the front-end origin per request for auth e-mail links

**Conflicts with a recorded decision:** [#46](https://github.com/BeshoyHindy/dotnet-saas-boilerplate/issues/46) (confirm, resend and reset links all take their base URL from `OriginOptions` through one `MailLinkOrigin`, never from the request).

- Here: `src/Modules/Identity/Modules.Identity/Services/MailLinkOrigin.cs:13-23`, used by `ForgotPasswordCommandHandler.cs:25`, `ResendConfirmationEmailCommandHandler.cs:26`, `RegisterUserCommandHandler.cs:29` and `UserRegisteredConfirmationMailHandler.cs:57`. The deploys wire it to the dashboard on purpose: `docker-compose.yml:200-202` ("the origin … is the DASHBOARD's, never the operator console's") and `deploy/dokploy/app.compose.yml:68`.
- Upstream: a new `FrontendOptions` (`AllowedOrigins` + `DefaultOrigin`) and an `IFrontendOriginResolver`. Self-service flows echo the request's `Origin` header when it is on the allow-list. Operator-driven flows use `DefaultOrigin`, which falls back to the API origin and then to the request host. It took nine fix-up commits inside the PR to reach a boot-safe state.
- **Does it expose a real bug here?** Not a security bug. #46's threat model (a request-influenced host inside an emailed link) still holds, and upstream's allow-list check is a weaker form of the same guarantee. What it does expose is a **UX gap the decision did not consider**, because #46 predates the two-client reversal (ADR-0008). The console ships `/forgot-password`, `/reset-password` and `/confirm-email` (`clients/console/src/routes.tsx:130-140`), but no mail can ever link to them. An operator (root tenant) who resets a password from the console receives a link to the dashboard.
- **A port that respects #46:** a server-side choice, e.g. `OriginOptions` gains a console URL and `MailLinkOrigin` picks it when the recipient's tenant is root. Never read the request's `Origin`. Size S. Worth doing only if operators are expected to self-serve password resets.

### `7e34c31`: replace MinIO with RustFS for local and test object storage

**Already have (solved differently).** Upstream replaced MinIO in local compose, the AppHost and the Testcontainers harness with `rustfs/rustfs:1.0.0`, and moved bucket bootstrap to `amazon/aws-cli`. This repo hit the same pull failure and fixed it on 2026-09-26 in `928decc`: `cgr.dev/chainguard/minio` pinned by digest at every site (`src/Host/Boilerplate.AppHost/AppHost.cs:72-101`, `docker-compose.yml`, `deploy/dokploy/data-services.compose.yml`, both integration factories). A root one-shot, `minio-volume-owner`, chowns existing volumes, and `MinioImagePinningTests` plus the compose contract pin the digest.

Differences that matter for a later decision, not now:
- This repo also runs MinIO in the production Dokploy data stack. Upstream's change touches only local and test, so switching to RustFS here would also mean migrating a production volume.
- The Chainguard free tier publishes only `:latest`, so the digest pin never moves unless someone bumps it by hand.

No recorded decision is involved (`928decc` was a fix, not a ticket). **No port recommended.**

### `bf86648`: correct idempotent replay payload and serialize concurrent duplicates

This repo rewrote the filter in [#82](https://github.com/BeshoyHindy/dotnet-saas-boilerplate/issues/82), [#84](https://github.com/BeshoyHindy/dotnet-saas-boilerplate/issues/84) and [#85](https://github.com/BeshoyHindy/dotnet-saas-boilerplate/issues/85). Upstream's commit bundles about fifteen fixes; each was checked against `src/BuildingBlocks/Web/Idempotency/IdempotencyEndpointFilter.cs`.

**(a) Already have.**

| Upstream fix | Here |
|---|---|
| cache the wire body and the real status, not the `IResult` wrapper | `CaptureAsync` executes the result into a buffer (`:334-360`) |
| write and probe through one store (HybridCache vs `IDistributedCache`) | `IDistributedCache` both ways (`:27-36`, `:262`, `:419`) |
| store before writing to the client, on `CancellationToken.None` | `:207-220`, `:416-419` |
| replay `Location` / `ETag`; never `Set-Cookie` | allow-list `:135`, re-filtered on replay `:571-580` |
| do not store non-2xx | `:214` |
| scope to the operation, route values and caller | subject + method + `Request.Path` (the concrete path, so route values are included), hashed (`:230-244`) |
| tenant from the resolved context, not the claim | `CacheKeyScope` from the ambient tenant (`:232`, `:243`) |
| probe fails open; unreadable entry is a miss | `:262-269`, `:276-288` |
| ProblemDetails for the 400 on an over-long key | `:158-166` |
| no idempotency on anonymous routes, enforced | #84: `IdempotentEndpointAnonymityTests`, `AnonymousRoutesAreNeverIdempotentTests`; `SelfRegisterUserEndpoint.cs:23` |
| a replay window capped for the presigned-URL endpoint | #85 chose the other option: `RequestUploadUrl` is not idempotent at all (`RequestUploadUrlEndpoint.cs:19`) |

This repo also has two things upstream lacks: a request fingerprint with a 422 on key reuse with a different payload (`:290-298`), and secrets excluded from that fingerprint (`:56-62`).

**(b) Conflicts with #82: Redis `SET NX` reservation across instances.** `KeyedAsyncLock.cs:8-14` documents on purpose that duplicates are serialised only within one process, and names `SET NX` as the fix "if that window ever matters". Nothing exposes a live bug here: `deploy/dokploy/app.compose.yml` sets no `replicas`, so the shipped deploy runs one API instance. `IConnectionMultiplexer` is already registered (`Caching/Extensions.cs:73`), so the port stays cheap if the project ever scales out. **Not recommended now.**

**(c) Port: detach the client-abort token.** Verified structurally here. The handler runs with the arguments as bound (`next(context)` at `:204`), so a `CancellationToken` parameter is `RequestAborted`. The capture writes under `RequestAborted` too (`:384`, `:390`, and typed results use it internally). If the client disconnects after the handler commits (for example, `RegisterUser` commits its transaction and outbox row) but before it returns, the next await throws, nothing is stored, and the retry runs the side effect again. Here the unique email or tenant id then answers the retry with a 4xx instead of the original 201. Upstream also claims that a cancelled capture can cache an *empty* 2xx body for the full TTL. That claim rests on upstream's regression test and was **not reproduced here**; the port's first step is to bring that test over. Fix: swap the bound `CancellationToken` argument and `HttpContext.RequestAborted` for a non-cancellable token before `next(context)`, and run the capture detached. Upstream also passes through untouched a handler that started the response itself (`Response.HasStarted`). Neither idempotent endpoint here does that, so it is optional. Size S. **The filter lives in `src/BuildingBlocks`, so this needs approval under `buildingblocks-protection.md`.**

### `921be0e`: mask sensitive entity-diff values and add an `IAuditExempt` opt-out

**Port, priority P1. The bug is present here.**

- `src/Modules/Auditing/Modules.Auditing/Persistence/EntityDiffBuilder.cs:120-124` builds `PropertyChange` with `OldValue: oldVal, NewValue: newVal` verbatim and only *flags* `IsSensitive` (`:176-179`: password / secret / token).
- The interceptor is attached to every module context: `AuditingModule.cs:62` registers it as `ISaveChangesInterceptor`, and `BuildingBlocks/Persistence/PersistenceExtensions.cs:65` adds all of them to each `AddHeroDbContext`, including `IdentityDbContext` (`IdentityModule.cs:126`).
- `JsonMaskingService` is wired only into the HTTP body path (`AuditHttpMiddleware.cs:71,107`), not into entity diffs.
- So every Identity write copies `PasswordHash` (and `SecurityStamp`, which is not even on the keyword list) plus the session token hashes into `AuditRecords`. They are readable through the audit endpoints to anyone holding `AuditingPermissions.AuditTrails.View` in that tenant (`GetAuditByIdEndpoint.cs:23`).

Port: mask flagged values to `****` (keep null as null) and add `IAuditExempt` (skip the entity in `AuditingSaveChangesInterceptor.cs:35`). Reuse the canonical `SensitiveFieldNames` in `BuildingBlocks/Shared/Security/SensitiveFieldNames.cs` (read-only use) rather than a third keyword list, and decide whether its whole-word `code` / `key` matches are too broad for audit diffs. Size S, all in the Auditing module. Existing rows are not cleaned by the fix; say so in the port ticket.

### `6f8d38d` + `27f23d6`: real HTML mail with a text alternative; one HTML shell and one encoder

**Port (one slice).** The bug is present, partly.

- `BuildingBlocks/Mailing/MailRequest.cs:5-11` has one `Body`. `SmtpMailService.cs:113` always sends it as `HtmlBody`, and `SendGridMailService.cs:36-42` sends the same string as both the plain and the HTML part.
- `Identity/Events/UserRegisteredEmailHandler.cs:37-40` interpolates `@event.FirstName` **unencoded** into what goes out as HTML. The value is caller-supplied on self-registration, so this is HTML injection into a mail. It is low severity, because the registrant also controls the recipient address.
- `Identity/Services/UserPasswordService.cs:54-57` sends a plain-text sentence containing the raw reset URL as HTML, which is not a clickable link and has unescaped `&`.
- Already correct: `ConfirmationMailBuilder.cs:71-112` HTML-encodes the name and URL, and `Notifications/.../TenantLifecycleEmailBodies.cs:19-44` uses `Escape(...)`.

Port: `MailRequest` gains an optional text body. SMTP builds `multipart/alternative`, SendGrid passes the text part. Add one `HtmlEmail` shell and encoder in Mailing, and move the three hand-built bodies onto it (upstream's Billing bodies do not apply). Size M. **It touches `src/BuildingBlocks/Mailing`, so it needs approval.**

### `dcb3525`: allow PATCH and the client headers under the restricted CORS policy

**Port the PATCH half (XS); the headers half does not apply.**

- PATCH endpoints exist: `ToggleUserStatusEndpoint.cs:17` and `ChangeFileVisibilityEndpoint.cs:15`, and both clients call them (`clients/*/src/api/identity.ts:72`, `files.ts:66`). `appsettings.Production.json:69-72` and `appsettings.json:110-111` list `GET, POST, PUT, DELETE` only.
- This does not bite the shipped deploys, which are same-origin: nginx in each client container proxies `/api` (`deploy/dokploy/app.compose.yml:156-161`), and Development uses `AllowAll`. It would break the first project that points a client's `apiBase` at the API cross-origin.
- Headers: upstream adds `tenant`, `x-fsh-app`, `idempotency-key` and the SignalR headers. None are sent here. The tenant header is gone (ADR-0002), and the console's `X-Console-As-Operator` is a client-side sentinel stripped before the request leaves (`clients/console/src/lib/api-client.ts:63-74`, deleted at `:230`).

### `d02a50b`: make `AmbientDbTransactionRegistry` actually receive transaction events

**Port. The bug is present verbatim; it stays hidden on PostgreSQL.**

- `src/BuildingBlocks/Persistence/AmbientDbTransactionRegistry.cs:25-29` declares `void TransactionStarted(DbConnection, TransactionEndEventData)` and `void TransactionUsed(...)`. Neither matches `IDbTransactionInterceptor` (which returns `DbTransaction` and takes a `result`), and there are no async overloads. The interface has default no-ops, so these compile and are never called. The registry stays empty.
- Effect here: `EfCoreOutboxStore.EnlistInAmbientTransactionAsync` (`Eventing/Outbox/EfCoreOutboxStore.cs:198-219`) always finds nothing. The outbox row lands in the business transaction only because every context in the scope shares one `DbConnection` (`PersistenceExtensions.cs:59-62`), each outbox save is a single `INSERT` (so EF opens no transaction of its own), and Npgsql runs the command in the connection's open transaction. #86's "one transaction with the outbox row inside it" holds by that accident, not by the registry. A two-statement outbox save would make EF call `BeginTransaction` on a connection that already has one, which Npgsql rejects (by our reading of EF Core's batching; not tested here).
- Port: upstream's exact-signature sync and async hooks, identity-based `Forget` for disposed transactions, and a reflection test that fails if any hook falls back to the interface default. Size S. Needs `src/BuildingBlocks` approval.

### `eb01b14`: reject stale profile updates with ETag / If-Match

**Port. The lost update is present.**

- `PUT /identity/profile` (`UpdateUserEndpoint.cs:18`) is a full-representation update: `UserProfileService.cs:78-129` assigns every field from the request.
- `RefreshSignInAsync` runs before the success check (`:124` vs `:126-129`).
- A failed `UpdateAsync`, including Identity's `ConcurrencyFailure`, becomes `CustomException("Update profile failed")`, which defaults to **500** (`BuildingBlocks/Core/Exceptions/CustomException.cs:34-35`).
- No ETag or If-Match exists anywhere in `src/` or the clients.
- `AspNetUsers.ConcurrencyStamp` is already mapped (`InitialIdentity.cs:82`), so no migration is needed.

Port: publish the stamp as a strong `ETag` on `GET /identity/profile`. Honour an optional `If-Match` on the PUT, checked right after load and before any storage call (upstream shows why: the avatar upload and delete happen first). Map `ConcurrencyFailure` to 412, move `RefreshSignInAsync` after the guard, and have both clients echo the tag and retry once on 412. Size M, and it is an API-surface change: re-export OpenAPI and regenerate both clients. Exposing `ETag` cross-origin needs `WithExposedHeaders("ETag")` in `BuildingBlocks/Web/Cors/Extensions.cs` (approval), plus `if-match` in `AllowedHeaders`. The shipped same-origin deploys work without either.

### `401fc1c`: stop the idle full-screen repaint from the aurora background animation

**Port to the console only (XS).** `clients/console/src/styles/globals.css:1044-1069` still animates `body` `background-position` for 72s on an infinite loop (`app-aurora`) and carries the unused `app-dot-pulse` keyframes. `clients/dashboard/src/styles/globals.css` has neither.

### `c6ccb81`: mount the Postgres 18 volume where the image expects it

**Already have.** `docker-compose.yml:31-41` and `deploy/dokploy/data-services.compose.yml:23-33` run `postgres:18-alpine` with `pg_data:/var/lib/postgresql`. Unrelated drift noticed while checking: both integration factories still use `postgres:17-alpine` (`Integration.Tests/Infrastructure/AppWebApplicationFactory.cs:38`, `Integration.Middleware.Tests/Infrastructure/MiddlewareWebApplicationFactory.cs:52`).

### `a0813e6`, `d7cb557`, `69dba68`, `894f1a2`: dependencies

See the next section.

## Version bumps and breaking changes

Input for the dependency-upgrade ticket. Current versions are from `src/Directory.Packages.props` (line numbers below), `src/Host/Boilerplate.AppHost/Boilerplate.AppHost.csproj:1` and `global.json` (SDK 10.0.100, `latestFeature`).

**Security:** `dotnet list src/Boilerplate.slnx package --vulnerable --include-transitive` reports **no vulnerable package** (run 2026-09-26), and restore runs with NuGetAudit on under `-warnaserror`. Every advisory named in the upstream commits is already cleared here:

| Advisory | Upstream fix | Here |
|---|---|---|
| GHSA-23fw-v26w-5fgq (SourceLink → `Microsoft.Build.Tasks.Git`) | `d7cb557`: 8.0.0 → 10.0.401 | `Microsoft.SourceLink.GitHub` 10.0.112 (props L17-20, patched 10.0.1xx line, #22) |
| GHSA-q939-rpr3-3284, GHSA-mggc-4xg6-vcxf (SSH.NET via Testcontainers) | `69dba68`: Testcontainers 4.11 → 4.14 | transitive pin `SSH.NET` 2026.0.0 (L154-156); Testcontainers still 4.11.0 (L123-125). Bumping Testcontainers ≥ 4.14 makes the pin removable |
| five `System.Security.Cryptography.Xml` GHSAs | pin 10.0.10 → 10.0.12 | pin 10.0.10 (L157-161), clean today; bump in lockstep with the 10.0.x line |
| GHSA-hv8m-jj95-wg3x (MessagePack), GHSA-v5pm-xwqc-g5wc (Microsoft.OpenApi), GHSA-2m69-gcr7-jv3q (SQLite) | pins moved within their lines | pins at 2.5.301 / 2.9.0 / 3.50.3 (L137-153), clean |

**Frontend (`894f1a2`):** already at or above every target in both clients: `react-router-dom ^7.18.4` (`clients/{dashboard,console}/package.json:35`), and `postcss@8.5.28`, `js-yaml@4.3.2`, `nanoid@3.3.19`, `brace-expansion@1.1.21` all resolve in both `pnpm-lock.yaml` files.

**Backend (`a0813e6`), where this repo is behind** (packages removed here are omitted: QuestPDF, RabbitMQ, SignalR, FeatureManagement, Http.Resilience, ServiceDiscovery, the CLI's Spectre/System.CommandLine, EF/Redis OTel instrumentation, `Asp.Versioning.Mvc`):

| Package | Here | Upstream now | Note |
|---|---|---|---|
| `Aspire.AppHost.Sdk` + `Aspire.Hosting.*` | 13.4.0 (csproj:1, L25-27) | 13.5.4 | **must move together**; mixed 13.4/13.5 fails at runtime. Upstream adds `NoWarn ASPIRE010` to the AppHost |
| .NET platform: EF Core, JwtBearer, Mvc.Testing, DataProtection/Caching.StackExchangeRedis, `Microsoft.Extensions.*` | 10.0.8 (L53, L68, L72, L86, L116) | 10.0.12 | patch line |
| `Npgsql.EntityFrameworkCore.PostgreSQL` / `Npgsql.OpenTelemetry` | 10.0.1 / 10.0.2 (L58, L31) | 10.0.3 / 10.0.3 | |
| `Microsoft.Extensions.Caching.Hybrid` | 10.6.0 (L70) | 10.10.0 | |
| OpenTelemetry exporter / hosting | 1.19.0 (L32-33) | 1.19.1 | instrumentation already 1.19.0 |
| `Asp.Versioning.Http` / `.Mvc.ApiExplorer` | 10.0.0 (L90-91) | 10.2.x | upstream suppresses new analyzers `AV0029`/`AV0030` in `Directory.Build.props` |
| `Scalar.AspNetCore` | 2.14.14 (L97) | 2.17.10 | |
| `Microsoft.OpenApi` (pin) | 2.9.0 (L143) | 2.12.2 | stay on 2.x (the comment at L138-142 explains why) |
| Hangfire | 1.8.23 (L62) | 1.8.25 | |
| MailKit / MimeKit | 4.17.0 (L80-81) | 4.18.0 / 4.18.1 | |
| `AWSSDK.S3` | 4.0.23.4 (L76) | 4.0.103.4 | |
| Serilog | 4.3.1 (L37) | 4.4.0 | |
| `SonarAnalyzer.CSharp` | 10.27.0.140913 (L21) | 10.34.0.3385 | brings new rules; see below |
| Testcontainers.* | 4.11.0 (L123-125) | 4.15.0 | lets the SSH.NET pin go |
| `Microsoft.NET.Test.Sdk` / `coverlet.collector` | 18.5.1 / 10.0.0 (L119, L13) | 18.10.1 / 10.0.1 | |
| **NSubstitute** | 5.3.0 (L121) | **6.2.0** | major |
| **xunit.runner.visualstudio** | 3.1.5 (L127) | **4.0.0** | major (still runs xUnit v2, per upstream) |
| MessagePack / SQLitePCLRaw / Cryptography.Xml (pins) | 2.5.301 / 3.50.3 / 10.0.10 | 2.5.305 / 3.53.3 / 10.0.12 | within their lines |

`StackExchange.Redis` 2.x → 3.3.1 (major, RESP3 by default) is a direct reference upstream only. Here it comes in transitively through the `*.StackExchangeRedis` packages, so it moves only if those do.

**Code changes that ride with the bump** (the breaking part for `-warnaserror`):
- **Sonar S8969** (redundant null-forgiving operator): upstream removed 157 `!`. The same sites exist here, e.g. `clientId!` in the token handlers, and each becomes a build error under `-warnaserror` once Sonar is bumped. Mechanical.
- **Sonar S8949**: `src/Modules/Multitenancy/Modules.Multitenancy/Provisioning/TenantProvisioningService.cs:68` enqueues `job.RunAsync(tenant.Id, correlationId)`. It must pass `CancellationToken.None` explicitly (the three-argument signature already exists, `TenantProvisioningJob.cs:38`). One line.
- `Directory.Build.props` `NoWarn` gains `AV0029;AV0030`, and the AppHost gains `ASPIRE010`.
- Most of the roughly 70 files upstream touched are in modules removed here (Billing, Catalog, Chat, Tickets, Webhooks) and do not apply.

## Open PRs

### PR #1376: database-backed Data Protection key store

**Skip (already have what it protects against).** Keys persist to Valkey (`BuildingBlocks/Caching/Extensions.cs:83-85`, with a pinned application name). Valkey runs with `--appendonly yes` and no `maxmemory` (`docker-compose.yml:53`, `deploy/dokploy/data-services.compose.yml:52`), so the default policy is no eviction, and upstream's "a cache can evict a key" risk does not apply as shipped. The PR does not build without #1372 and #1374, both out of scope for this map. One caveat worth a docs line: an operator who sets an `allkeys-*` eviction policy on that Valkey would lose the key ring along with the cache.

### PR #1374: Microsoft SQL Server as an opt-in provider

**Skip.** Out of scope per map #87 (PostgreSQL only, decided in [#6](https://github.com/BeshoyHindy/dotnet-saas-boilerplate/issues/6)). The only part of value, the registry fix, was extracted upstream as `d02a50b` (ported above).

### PR #1372: CLI framework packages, `.agents` scaffolding, fork-local templates

**Skip.** Out of scope per map #87: scaffolding is `dotnet new saas` (ADR-0001), and the CLI was removed in [#4](https://github.com/BeshoyHindy/dotnet-saas-boilerplate/issues/4).

### Localization PRs #1381–#1384

Shape and size only; whether the template ships i18n is a later decision (map #87, "Not yet specified").

| PR | What | Size | Depends on |
|---|---|---|---|
| #1381, framework (1/4) | `SupportedCultures` allow-list, `UseRequestLocalization` (UI culture only), a culture provider reading a JWT `locale` claim, localized `ProblemDetails` (title, detail and a `code` extension), localizable exception types, a nullable `Locale` column on the user plus migration, validated on profile update. Also sets `PredefinedCulturesOnly=false` for ICU-less images (this repo's `chiseled-extra` image already has ICU). | +4050/−90, 66 files, **19 of them in `src/BuildingBlocks`** | none |
| #1382, module catalogs (2/4) | one resource catalog per module (en-US + pt-BR), message keys at throw and validation sites, catalog-parity tests | +9250/−585, 301 files (237 its own) | #1381. **Touches Billing, Catalog, Chat, Tickets and Webhooks, all removed here**; about half its files would drop |
| #1383, operator app (3/4) | react-i18next, per-feature JSON catalogs with key and placeholder parity tests, a language switcher, `Accept-Language` on requests, runtime `defaultLanguage` | +6066/−1625, 122 files | #1381 for server-side persistence (works from localStorage without it) |
| #1384, tenant app (4/4) | same shape for the dashboard | +8926/−2463, 141 files | #1381, #1382 (`code` on ProblemDetails) |

Adopting it here would mean re-cutting #1381 against this repo's BuildingBlocks (the new user column needs a migration), re-scoping #1382 to the five kept modules, and mapping #1383 from upstream's `clients/admin` to `clients/console`. That is several L slices.

### PRs #1356 and #1348

**Skip.** #1356 fixes `clients/dashboard/src/pages/activity.tsx`, which does not exist here. #1348 fixes Catalog SSE broadcasting; Catalog was removed in #4, SSE in #6, and no `sse` directory exists under `clients/`.

## Recorded decisions upstream challenges

- **#13 (hardening: never honour `X-Forwarded-Host`, fail closed):** not challenged. `ee6a974` reaches the same end state with weaker defaults.
- **#46 (mail links from `OriginOptions`, never the request):** holds. `6221ffe` exposes a UX gap, not a security bug: the console's auth pages are unreachable from mail, because #46 predates ADR-0008's second client.
- **#82 (idempotency filter owns its entry; single-process lock):** the part it decided (no cross-instance `SET NX`) holds, since one replica ships. `bf86648` exposes a gap it did not consider, the client-abort token. That is a port inside the decision, not a reversal.
- **#6 (PostgreSQL only):** holds. `d02a50b` is a real bug that PostgreSQL happens to mask, so it is worth fixing anyway.
- **`928decc` (MinIO from Chainguard):** not a ticket decision; RustFS is an alternative, not a fix.

None of the upstream changes exposes a security bug in a recorded decision. The two security-relevant findings (`921be0e` audit diffs, the `6f8d38d` mail encoding) are defects in code that no decision covered.

## Recommended ports

In priority order. Each is one build ticket unless grouped.

1. **Mask sensitive values in entity-change audit diffs and add `IAuditExempt`** (`921be0e`): P1 security, S, Auditing module only.
2. **Detach the client-abort token inside the idempotency filter** (`bf86648` part c): S, BuildingBlocks (approval). Start from upstream's disconnect regression test.
3. **Fix `AmbientDbTransactionRegistry`'s interceptor signatures** (`d02a50b`): S, BuildingBlocks (approval), with the reflection guard test.
4. **HTML mail with a text alternative, one encoder** (`6f8d38d` + `27f23d6`): M, Mailing (approval) plus Identity. Fixes the unencoded `FirstName` and the non-link reset mail.
5. **ETag / If-Match on `PUT /identity/profile`** (`eb01b14`): M, API surface (OpenAPI re-export, both clients). Includes the 412 mapping, moving `RefreshSignInAsync`, and CORS `ETag` exposure (approval).
6. **Small hygiene, can be grouped:** PATCH in the restricted CORS methods (`dcb3525`, XS); drop the console's aurora animation (`401fc1c`, XS); clear the forwarded-headers trust lists when proxy support is disabled (from `ee6a974`, XS, optional).
7. **Dependency upgrade** (`a0813e6`, feeds its own ticket): Aspire 13.5.4 as a unit, the 10.0.12 platform line, Testcontainers ≥ 4.14 (drop the SSH.NET pin), Sonar 10.34 with the S8969/S8949 fixes, and the NSubstitute / xunit-runner majors.

Deferred to a decision, not a port: a console origin for operator mail links (`6221ffe`, adapted to #46); RustFS (`7e34c31`); localization (#1381–#1384).
