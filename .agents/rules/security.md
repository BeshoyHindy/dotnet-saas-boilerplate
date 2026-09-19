# Web security & request governance

CORS, security headers, rate limiting, idempotency. `src/BuildingBlocks/Web/`.
For auth/JWT/permissions see `modules/identity.md`; for the global exception handler see `api-conventions.md`.

## CORS (`Web/Cors/`)

Policy `AppCorsPolicy`. **No `AllowCredentials()` in either branch** (decided in #13): auth is a bearer header, no client sends cookies or `withCredentials`, and the credentialed SignalR negotiate that once justified it is gone. Don't add it back without a client that needs it — and then only on the explicit-origins branch.

`CorsOptions.AllowAll=true` is **Development only**; `CorsOptionsValidator` fails the boot in every other environment. It uses `SetIsOriginAllowed(_ => true)` rather than `AllowAnyOrigin()` so the reflected origin is visible in traces. `UseHeroCors()` runs **before** `UseHttpsRedirection()` so OPTIONS preflight isn't 307-redirected.

## Security headers (`Web/Security/`)

`UseHeroSecurityHeaders()` sets `X-Content-Type-Options`, `X-Frame-Options: DENY`, `Referrer-Policy`, HSTS (HTTPS), and a CSP. `SecurityHeadersOptions.ExcludedPaths` defaults to `["/scalar","/openapi"]` (they manage their own scripts) — keep those excluded.

The headers are written from a **`Response.OnStarting` callback**, not eagerly: `UseExceptionHandler` resets status, body *and* headers before re-running the handler, so eager writes vanish from every 5xx. Never "simplify" it back to a straight-line write — `SecurityHeadersTests.ThrowEndpoint_Should_StillEmitSecurityHeaders…` fails if you do.

## Host filtering & the proxy (`Web/Security/ProxyOptions.cs`)

`AllowedHosts` is an explicit semicolon-separated list; `*` is rejected in Production by `ProductionConfigurationGuard`. Behind Traefik set `ProxyOptions.Enabled` so `UseForwardedHeaders` runs **first** in the pipeline; it clears the loopback-only defaults, and `ProxyOptionsValidator` then **fails the boot** unless `KnownProxies`/`KnownNetworks` names the proxy or `TrustAnyProxy` is set explicitly (opt-in, only where the app is unreachable except through the proxy). Trusting any peer on a shared container network lets a neighbour spoof `X-Forwarded-For`, which partitions rate limits and lands in the audit trail.

**`XForwardedHost` is never enabled.** Host filtering runs before the forwarded-headers middleware, so honouring it would let a caller rewrite `Request.Host` after the allow-list approved the real one. `ForwardedHeadersTests` locks this down.

**Every emailed link is built from `OriginOptions.OriginUrl`, never from the request.** Password reset, registration and resend-confirmation all resolve the base URL through `MailLinkOrigin.Require` in their command handlers (the endpoints no longer touch `HttpContext` for it), and the handler throws if no origin is configured. A mailed link is exactly where a swapped host turns into account takeover, so keep the request out of it. The registration mail points at the **client** route `{origin}/confirm-email?userId&code&tenant` — the page then calls `/api/v1/tenants/{tenant}/auth/confirm-email`; mailing the API route landed the recipient on a raw JSON body (#46).

## Request limits (`Web/Limits/`)

`RequestLimits` caps Kestrel's body (10 MiB), total headers and request line. Uploads go direct to object storage via presigned URLs, so the API never needs a large body — raise the cap only with a reason.

## Health checks (`Web/Health/`)

`/health/live` runs nothing, `/health/ready` runs only checks tagged `HealthTags.Ready`, `/health` runs everything. Tag a new check `Ready` only if the API cannot serve requests without it — every module DbContext check hits the same PostgreSQL server, so only the tenant catalog carries the tag.

## Rate limiting (`Web/RateLimiting/`)

Chained partitioned fixed-window limiter: **tenant → user → IP** (defaults 1000 / 200 / 300 per 60s) + a stricter named `"auth"` policy (10/60s). Health paths are unlimited. Rejection → 429 + ProblemDetails + `Retry-After`. `RateLimitingOptions.Enabled` is read **eagerly** — when false the middleware is skipped entirely (tests set it via env var before host build).

## Idempotency (`Web/Idempotency/`)

Opt-in per endpoint with **`.WithIdempotency()`**. Reads the `Idempotency-Key` header (max 128 chars, 24h TTL); replays return the cached response with `Idempotency-Replayed: true`. The entry is scoped to the **resolved** tenant by the Caching block (`CacheKeys.IdempotencyEntry`), which covers the authenticated routes and the anonymous `tenants/{tenant}/auth` ones alike; a request with no tenant at all goes to `GlobalHybridCache` explicitly, partitioned by the authenticated subject. Put it on POSTs that must be replay-safe (e.g. CreateTenant).

