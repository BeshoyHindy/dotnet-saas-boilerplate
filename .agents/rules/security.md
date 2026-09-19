# Web security & request governance

CORS, security headers, rate limiting, idempotency. `src/BuildingBlocks/Web/`.
For auth/JWT/permissions see `modules/identity.md`; for the global exception handler see `api-conventions.md`.

## CORS (`Web/Cors/`) — the credentialed-request gotcha

Policy `AppCorsPolicy`. When `CorsOptions.AllowAll=true` it uses **`SetIsOriginAllowed(_ => true).AllowAnyHeader().AllowAnyMethod().AllowCredentials()`** — deliberately **NOT `AllowAnyOrigin()`**. `Access-Control-Allow-Origin: *` is illegal with credentialed requests, so `AllowAnyOrigin()` silently breaks any call the browser sends with credentials while the rest keeps working. Never "simplify" it to `AllowAnyOrigin()`. `UseHeroCors()` runs **before** `UseHttpsRedirection()` so OPTIONS preflight isn't 307-redirected.

## Security headers (`Web/Security/`)

`UseHeroSecurityHeaders()` sets `X-Content-Type-Options`, `X-Frame-Options: DENY`, `Referrer-Policy`, HSTS (HTTPS), and a CSP. `SecurityHeadersOptions.ExcludedPaths` defaults to `["/scalar","/openapi"]` (they manage their own scripts) — keep those excluded.

The headers are written from a **`Response.OnStarting` callback**, not eagerly: `UseExceptionHandler` resets status, body *and* headers before re-running the handler, so eager writes vanish from every 5xx. Never "simplify" it back to a straight-line write — `SecurityHeadersTests.ThrowEndpoint_Should_StillEmitSecurityHeaders…` fails if you do.

## Host filtering & the proxy (`Web/Security/ProxyOptions.cs`)

`AllowedHosts` is an explicit semicolon-separated list; `*` is rejected in Production by `ProductionConfigurationGuard`. Behind Traefik set `ProxyOptions.Enabled` so `UseForwardedHeaders` runs **first** in the pipeline; it clears the loopback-only defaults, so either list `KnownProxies`/`KnownNetworks` or set `TrustAnyProxy` when the container is reachable only through the proxy.

## Request limits (`Web/Limits/`)

`RequestLimits` caps Kestrel's body (10 MiB), total headers and request line. Uploads go direct to object storage via presigned URLs, so the API never needs a large body — raise the cap only with a reason.

## Health checks (`Web/Health/`)

`/health/live` runs nothing, `/health/ready` runs only checks tagged `HealthTags.Ready`, `/health` runs everything. Tag a new check `Ready` only if the API cannot serve requests without it — every module DbContext check hits the same PostgreSQL server, so only the tenant catalog carries the tag.

## Rate limiting (`Web/RateLimiting/`)

Chained partitioned fixed-window limiter: **tenant → user → IP** (defaults 1000 / 200 / 300 per 60s) + a stricter named `"auth"` policy (10/60s). Health paths are unlimited. Rejection → 429 + ProblemDetails + `Retry-After`. `RateLimitingOptions.Enabled` is read **eagerly** — when false the middleware is skipped entirely (tests set it via env var before host build).

## Idempotency (`Web/Idempotency/`)

Opt-in per endpoint with **`.WithIdempotency()`**. Reads the `Idempotency-Key` header (max 128 chars, 24h TTL); replays return the cached response with `Idempotency-Replayed: true`. Cache key is tenant-scoped (`CacheKeys.IdempotencyEntry`). Put it on POSTs that must be replay-safe (e.g. CreateTenant).

