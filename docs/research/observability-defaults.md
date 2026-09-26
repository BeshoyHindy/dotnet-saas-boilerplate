# Observability defaults a first product inherits

Research for [#128](https://github.com/BeshoyHindy/dotnet-saas-boilerplate/issues/128), part of the map [#87](https://github.com/BeshoyHindy/dotnet-saas-boilerplate/issues/87).

- **This repo:** `develop` at `83f43cc`, reviewed on branch `research/observability-defaults`.
- **Method:** read the code under `src/BuildingBlocks/Web/Observability/` (Serilog + OpenTelemetry wiring), the health-check registrations across every module, `GlobalExceptionHandler`, the Hangfire and outbox/eventing telemetry, and the observability-relevant environment wiring in `docker-compose.yml` and `deploy/dokploy/`. One claim about `Serilog.Sinks.OpenTelemetry`'s default endpoint is backed by the sink's own source (`OpenTelemetrySinkOptions.cs`, `serilog/serilog-sinks-opentelemetry` on GitHub — an empty/whitespace `Endpoint` is treated as unset and the constructor's own default is `http://localhost:4317`). Findings only — no fixes were applied; sizes are rough estimates for the decision ticket that graduates from this one ([#132](https://github.com/BeshoyHindy/dotnet-saas-boilerplate/issues/132)).
- **Out of scope by instruction:** Aspire containers, first compose cost, the `libgssapi` log, the per-process idempotency lock, dependency versions, the object store (all recorded known limits or already-ticketed); masking sensitive values in entity-change audit diffs ([#103](https://github.com/BeshoyHindy/dotnet-saas-boilerplate/issues/103), already a build ticket — the Auditing module's diff builder was read but is not re-derived here); backups/restore, migration-on-deploy behaviour, resource limits and restart policies, graceful shutdown, secrets handling, log retention/disk growth and the zero-to-running path, which are [#131](https://github.com/BeshoyHindy/dotnet-saas-boilerplate/issues/131)'s ground; EF/performance concerns (N+1s, pagination, caching), which are [#129](https://github.com/BeshoyHindy/dotnet-saas-boilerplate/issues/129)'s ground.

Sizes: **S** — a handful of lines in one or two files. **M** — a new component or a change that spans a building block plus its config. **L** — a genuinely new subsystem.

## Summary

| # | Area | Location | Severity | Fix size |
|---|---|---|---|---|
| O1 | Unhandled 500s log with no exception | `src/BuildingBlocks/Web/Exceptions/GlobalExceptionHandler.cs:97-102` | High | S |
| O2 | Enrichment is computed but never rendered | `appsettings.json:41`, `appsettings.Production.json:22`, `HttpRequestContextEnricher.cs` | High | S |
| O3 | Every authenticated log line carries the user's email | `src/BuildingBlocks/Web/Observability/Logging/Serilog/HttpRequestContextEnricher.cs:39` | Medium | S |
| O4 | `Logging:LogLevel` in appsettings is dead under Serilog | `src/BuildingBlocks/Web/Observability/Logging/Serilog/Extensions.cs:24-31` | Medium | S |
| O5 | Telemetry is built but exported nowhere by default | `appsettings.Production.json:2-15`; `deploy/dokploy/app.compose.yml:104-107`; `.env.example:124-132` | High | M |
| O6 | Empty-endpoint Serilog OTel sink silently targets `localhost:4317` | `appsettings.Production.json:18,23` | Medium | S |
| O7 | Hangfire job spans are disconnected root traces | `src/BuildingBlocks/Jobs/HangfireTelemetryFilter.cs` | Medium | S–M |
| O8 | Outbox dispatch has no spans and no backlog metric | `src/BuildingBlocks/Eventing/Telemetry/EventingTelemetry.cs`; `Outbox/OutboxDispatcher.cs:97,101` | Medium | M |
| O9 | Integration-event `CorrelationId` is a fresh GUID, not the request's | `GenerateTokenCommandHandler.cs:115`; `UserRegistrationService.cs:491` | Low | S |
| O10 | `/health` and `/health/ready` are anonymous and rate-limit-exempt | `src/BuildingBlocks/Web/Health/HealthEndpoints.cs:15-18` | Medium | S |
| O11 | Every exception-path response is logged at Error, 4xx included | `src/BuildingBlocks/Web/Exceptions/GlobalExceptionHandler.cs:102` | Medium | S |
| O12 | No access/request log | `src/BuildingBlocks/Web/Observability/Logging/Serilog/Extensions.cs` (no `UseSerilogRequestLogging` call anywhere) | Low | S |
| O13 | `ProblemDetails.Title` leaks the raw exception type name | `src/BuildingBlocks/Web/Exceptions/GlobalExceptionHandler.cs:48` | Low | S |

Counts: **13 findings** — 3 high · 7 medium · 3 low.

## Findings

### O1. Unhandled 500s are logged with no exception attached

`src/BuildingBlocks/Web/Exceptions/GlobalExceptionHandler.cs:102`:

```csharp
logger.LogError("Exception at {Path} - {StatusCode} {Title}", httpContext.Request.Path.Value?.Replace(Environment.NewLine, string.Empty), statusCode, problemDetails.Title);
```

The `exception` parameter caught by `TryHandleAsync` is never passed to `LogError`. For the generic 500 branch (lines 79-85), `Title` is always the fixed string `"An unexpected error occurred"`, so the log line carries no exception type, no message, and — because the default Serilog message template only fills `{Exception}` when an `Exception` object is actually passed to the log call — no stack trace either. Lines 97-100 push `exception_stackTrace` and friends onto `LogContext`, but (see O2) nothing in the shipped configuration ever renders pushed properties, so that data is invisible too. Net effect: the one log line a first product gets for its most severe class of failure reads `Exception at /api/v1/x - 500 An unexpected error occurred` and nothing more — no way to tell *which* exception happened without attaching a debugger or turning on export (O5).

**Severity: high** — this is the exact signal the ticket asks about ("how an unhandled error surfaces"), and it is the one place it is weakest. **Fix size: S** — pass `exception` as the first argument to `LogError` (`logger.LogError(exception, "...")`), which restores type, message and stack trace via the default template's `{Exception}` token with no other changes needed.

### O2. Structured enrichment is computed but never rendered anywhere an operator looks

- The Console sink's `Args` never set an `outputTemplate` or a JSON formatter: `src/Host/Boilerplate.Api/appsettings.json:41-45` and `appsettings.Production.json:22` both read `{ "Name": "Console", "Args": { "restrictedToMinimumLevel": "Information" } }`.
- Serilog's built-in default console template is `{Timestamp:HH:mm:ss} [{Level:u3}] {Message:lj}{NewLine}{Exception}` — it interpolates only the rendered message and (when present) the exception; it does not print any of the properties attached to the event.
- Meanwhile `appsettings.Production.json:19` enriches every event with `FromLogContext`, `WithMachineName`, `WithThreadId`, `WithCorrelationId`, `WithProcessId`, `WithProcessName`, and `src/BuildingBlocks/Web/Observability/Logging/Serilog/HttpRequestContextEnricher.cs:27-40` adds `RequestMethod`, `RequestPath`, `UserAgent`, and — for authenticated calls — `UserId`, `Tenant`, `UserEmail`. Serilog 4 also captures `TraceId`/`SpanId` natively from `Activity.Current`.
- None of it reaches stdout, which is the only sink shipped by default (see O5): the console line for a request from tenant `acme`, user `42`, correlation id `abc-123` looks identical to one from an anonymous caller.

**Severity: high** — this directly negates the enrichment the ticket asks about: tenant/user/correlation-id enrichment exists in-process and is simply thrown away before it reaches the operator. **Fix size: S** — set an explicit `outputTemplate` that references the properties (e.g. `{Timestamp:o} [{Level:u3}] ({Tenant}/{UserId}/{CorrelationId}) {Message:lj}{NewLine}{Exception}`), or switch the Console sink to `Serilog.Formatting.Compact.CompactJsonFormatter`/`RenderedCompactJsonFormatter` so every property is machine-parseable — either is a one-line change to the `Console` `Args` in both `appsettings.json` and `appsettings.Production.json`.

### O3. Every authenticated log line carries the user's email

`src/BuildingBlocks/Web/Observability/Logging/Serilog/HttpRequestContextEnricher.cs:31-40`:

```csharp
if (httpContext.User?.Identity?.IsAuthenticated == true)
{
    var userId = httpContext.User.GetUserId();
    var tenant = httpContext.User.GetTenant();
    var userEmailId = httpContext.User.GetEmail();

    logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty("UserId", userId));
    logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty("Tenant", tenant));
    logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty("UserEmail", userEmailId));
}
```

Every log event produced while handling an authenticated request gets a `UserEmail` property — i.e. every single log line for the lifetime of the request, not just an audit or auth-specific one. Other handlers in this codebase deliberately avoid this: `TokenGeneratedLogHandler.cs:27`, `UserRegisteredConfirmationMailHandler.cs`, and `PasswordChangedEventHandler.cs` all log `UserId` rather than the email address specifically to minimize PII in logs. This enricher is the one place that pattern isn't followed, and — once O2 is fixed and the property actually renders, or the moment OTLP log export is turned on (O5) — the email address ships to every log line and every backend the logs reach.

**Severity: medium** — not currently visible anywhere (blocked by O2), but a real field that will surface PII broadly the moment either gap above is closed, and it's inconsistent with the minimization pattern the rest of the codebase follows. **Fix size: S** — drop the `UserEmail` property; `UserId` + `Tenant` already identify the caller for correlation without carrying PII.

### O4. `appsettings.json`'s `Logging:LogLevel` section has no effect

`src/BuildingBlocks/Web/Observability/Logging/Serilog/Extensions.cs:21-32`:

```csharp
builder.Services.AddSerilog((context, logger) =>
{
    var httpEnricher = context.GetRequiredService<HttpRequestContextEnricher>();
    logger.ReadFrom.Configuration(builder.Configuration);
    logger.Enrich.With(httpEnricher);
    logger
        .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
        .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
        .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Error)
        .MinimumLevel.Override("Hangfire", LogEventLevel.Warning)
        .MinimumLevel.Override("Finbuckle.MultiTenant", LogEventLevel.Warning)
        .Filter.ByExcluding(Matching.FromSource("Microsoft.AspNetCore.Diagnostics.ExceptionHandlerMiddleware"));
    ...
});
```

`AddSerilog` is called with no `writeToProviders: true`, so it replaces the default `Microsoft.Extensions.Logging` provider entirely — meaning the `"Logging": { "LogLevel": { ... } }` block that both `appsettings.json:47-52` and `appsettings.Production.json:26-33` still ship (`Microsoft.AspNetCore: Warning`, `Hangfire: Warning`, `Microsoft.EntityFrameworkCore: Warning`) is dead configuration: Serilog reads only its own `"Serilog"` section (`ReadFrom.Configuration`), and three of the category overrides that actually apply (`Microsoft`, `Microsoft.EntityFrameworkCore` → `Error`, `Hangfire`) are hardcoded in code rather than configurable from `appsettings.*.json` at all. An operator raising `Microsoft.EntityFrameworkCore` back to `Information` or `Warning` in `appsettings.Production.json` to chase a slow query or an EF warning would see the config change do nothing, because the hardcoded `Error` override always applies afterward.

**Severity: medium** — a plausible, silent footgun for a first product's own operator, not a security issue. **Fix size: S** — either delete the inert `Logging:LogLevel` block (so nothing implies it works) or move the three hardcoded overrides into `Serilog:MinimumLevel:Override` in config so they're actually tunable per environment.

### O5. Telemetry is fully wired but exported nowhere by default

- `src/Host/Boilerplate.Api/appsettings.Production.json:2-15`: `OpenTelemetryOptions.Enabled = true`, `Tracing.Enabled = true`, `Metrics.Enabled = true`, but `Exporter.Otlp.Enabled = false` and `Exporter.Otlp.Endpoint = ""`.
- `deploy/dokploy/app.compose.yml:104-107` passes both keys straight through from `${OTEL_EXPORTER_ENABLED}` / `${OTEL_EXPORTER_OTLP_ENDPOINT}`, and `deploy/dokploy/.env.example:124-132` ships both blank, with a comment explicitly noting "Production ships the OTLP exporter disabled ... set both or neither." Neither Dokploy compose file, nor the local `docker-compose.yml`, defines an otel-collector, Jaeger, Prometheus, Grafana, Loki or Seq service — there is nothing to point the exporter at even if an operator flips the switch, short of standing up their own.
- There is no Prometheus `/metrics` scrape endpoint either (no such exporter package or route in the tree).
- `src/BuildingBlocks/Web/Observability/OpenTelemetry/Extensions.cs:75-171` still builds the full metrics/tracing pipeline in Production regardless (ASP.NET Core, HttpClient, Npgsql, runtime instrumentation; the caching, auditing and outbox meters) — none of it is wasted CPU-wise (it's cheap in-process aggregation), but with export off, every span and every metric point is computed and discarded on every request.
- The practical consequence: out of the box, a first product's only operator-visible signal for anything — a slow endpoint, a spike in 5xx, a growing outbox backlog — is the degraded stdout log described in O1/O2. There is no dedicated error-rate metric anywhere in the exception path either (`GlobalExceptionHandler.cs` never increments a counter), so even a wired-up collector's dashboards would have to derive it from the generic ASP.NET Core `http.server.request.duration` histogram's status-code tag rather than a purpose-built one.

**Severity: high** — for a first product, "the template ships full OpenTelemetry instrumentation" reads very differently from "the template ships instrumentation with nowhere for it to go and no collector to point it at." **Fix size: M** — either ship an opt-in collector + a minimal viewer as part of the Dokploy stack (a genuinely new compose service, hence M rather than S), or explicitly document the empty defaults as a known limit with a docs section on wiring an external OTLP backend (`OTEL_EXPORTER_ENABLED=true` + `OTEL_EXPORTER_OTLP_ENDPOINT=...` already does the plumbing — the gap is that nothing is shipped to point them at, and this isn't spelled out anywhere a first-product operator would read before going live).

### O6. The empty-endpoint Serilog OpenTelemetry sink silently targets `localhost:4317`

`src/Host/Boilerplate.Api/appsettings.Production.json:17-24`:

```json
"Serilog": {
  "Using": [ "Serilog.Sinks.Console", "Serilog.Sinks.OpenTelemetry" ],
  ...
  "WriteTo": [
    { "Name": "Console", "Args": { "restrictedToMinimumLevel": "Information" } },
    { "Name": "OpenTelemetry", "Args": { "endpoint": "", "protocol": "grpc", "resourceAttributes": { "service.name": "Boilerplate.Api" } } }
  ]
}
```

This `WriteTo` array is read unconditionally by `logger.ReadFrom.Configuration(builder.Configuration)` (`Serilog/Extensions.cs:24`) — it does not consult `OpenTelemetryOptions.Enabled` or `Exporter.Otlp.Enabled` at all, those flags only gate the separate, code-driven sink added conditionally later in the same method (`Serilog/Extensions.cs:39-61`, only added when `ResolveOtlpLogExport` finds a real endpoint). `Serilog.Sinks.OpenTelemetry`'s own `OpenTelemetrySinkOptions` treats an empty/whitespace `Endpoint` exactly like an unset one and leaves it `null`, at which point the exporter falls back to its own compiled-in default, `http://localhost:4317` (confirmed against the sink's source on GitHub, `serilog/serilog-sinks-opentelemetry`). So in Production, regardless of whether OTLP export is meant to be on or off:

- A batch of logs is periodically assembled and sent to `localhost:4317` over gRPC, which nothing listens on inside the API container — the sends fail silently (Serilog sinks swallow failures by default) on every batch, forever, for no operator-visible reason.
- If an operator later does turn on OTLP via `OTEL_EXPORTER_ENABLED=true` + a real endpoint, the code-driven sink (`Serilog/Extensions.cs:41-60`) is added *in addition to* this config-driven one, so logs are batched and shipped twice — once correctly, once still pointed at `localhost:4317`.

The integration test suite works around exactly this by blanking the entry at test time (`Serilog:WriteTo:1:Name` set to an empty string in the test host factory), which is itself a signal that this default is not inert in practice.

**Severity: medium** — silent, wasted export attempts and a latent double-export bug, not a functional break (nothing crashes, no data is corrupted), but confusing to anyone reading the stack's outbound-connection logs or wondering why OTLP setup didn't change anything. **Fix size: S** — remove the static `OpenTelemetry` entry from `Serilog:WriteTo` in `appsettings.Production.json`; the code-driven conditional sink already covers every case the static entry was trying to (including Aspire's injected endpoint), with the added benefit of actually respecting `OpenTelemetryOptions.Enabled`.

### O7. Hangfire job spans are disconnected root traces

`src/BuildingBlocks/Jobs/HangfireTelemetryFilter.cs` (registered via `config.UseFilter(new HangfireTelemetryFilter())` at `src/BuildingBlocks/Jobs/Extensions.cs:79`) does start a real span per job execution — `OnPerforming` opens an `Activity` on the `Boilerplate.Hangfire` source tagged with job id/type/method, and `OnPerformed` sets its status (`Error` with exception type/message, or `Ok`) before disposing it. That instrumentation is real and already wired, contrary to a stale comment elsewhere in the codebase (`OpenTelemetry/Extensions.cs`'s "Hangfire/job instrumentation placeholder... wire hooks in jobs building block" — the hooks already exist).

What is missing: nothing captures `Activity.Current`'s trace context at the point a job is *enqueued* and carries it through to `OnPerforming`, so every job execution is always a fresh root trace with no link back to the HTTP request (or other job) that enqueued it. An operator investigating "why did tenant X's provisioning job fail" from the API request that triggered it cannot follow one trace end-to-end; they have to correlate by job id, tenant id, or timestamp instead.

**Severity: medium** — the instrumentation exists and is useful on its own; only the request-to-job trace linkage is missing. **Fix size: S–M** — capture `Activity.Current?.Context` (or the incoming trace id) into a Hangfire job parameter at enqueue time, then start the job's `Activity` as a child (or linked) span from that context in `OnPerforming` instead of an unparented one.

### O8. Outbox dispatch has no spans and no dispatched/backlog metric

- `src/BuildingBlocks/Eventing/Telemetry/EventingTelemetry.cs` registers exactly two counters on the `Boilerplate.Eventing` meter: `boilerplate.eventing.outbox.deadlettered` and `boilerplate.eventing.outbox.redriven`. There is no counter for a message dispatched successfully, and no gauge for how many outbox rows are currently pending.
- `Outbox/OutboxDispatcher.cs:97` and `:101` log a dead-letter (`LogError`) or a retry (`LogWarning`) with the message id and retry count, but not the event type or tenant, and there is no corresponding `LogInformation`/metric on the (presumably far more common) successful-dispatch path.
- No `ActivitySource` exists anywhere in the outbox/eventing code, so a dispatch attempt produces no span; nothing connects "this integration event was published here" to "this handler processed it there."

Combined, an operator has no way to see a growing backlog before it turns into a dead-letter (the only two visible states are "fine" and "already failed enough times to be dead-lettered"), and no trace links a published integration event to whatever eventually consumes it.

**Severity: medium** — the outbox is exactly the kind of async, easy-to-silently-back-up mechanism the ticket asks about, and today it is a black box between "published" and "dead-lettered." **Fix size: M** — add a `Counter<long>` for successful dispatch and an `ObservableGauge` (or a periodically-sampled counter) for pending-row count, plus a short-lived `Activity` per dispatch attempt tagged with event type and tenant.

### O9. Integration-event `CorrelationId` is a fresh GUID, not the originating request's

`src/Modules/Identity/Modules.Identity/Features/v1/Tokens/TokenGeneration/GenerateTokenCommandHandler.cs:115` and `src/Modules/Identity/Modules.Identity/Services/UserRegistrationService.cs:491` both mint `Guid.NewGuid().ToString()` as the event's `CorrelationId` at publish time, rather than reusing the current `Activity`'s trace id or the request's `X-Correlation-ID`. So even where O8's tracing gap is eventually closed, the one correlation field integration events already carry can't be joined back to the HTTP request or Serilog log lines that produced them — it's an id that only ever appears once, on the event itself.

**Severity: low** — a completeness gap on top of O8, not a new category of blind spot. **Fix size: S** — thread `Activity.Current?.TraceId` (or the inbound correlation id already resolved for the response, `GlobalExceptionHandler.cs:93-95`'s pattern) through to event construction instead of minting a new value.

### O10. `/health` and `/health/ready` are anonymous and exempt from rate limiting

`src/BuildingBlocks/Web/Health/HealthEndpoints.cs:15-18`:

```csharp
var group = app.MapGroup("/health")
               .WithTags("Health")
               .AllowAnonymous()
               .DisableRateLimiting();
```

This is deliberate and reasonable for `/health/ready` on its own (Traefik polls it every 10 seconds per `deploy/dokploy/app.compose.yml:136-138`, and a rate-limited readiness probe would be self-defeating). But the same group also serves `GET /health` — the full, on-demand report that fans out to every registered check (self, Hangfire, Redis when configured, and one representative `DbContext` check per module: Identity, Multitenancy's tenant catalog, Files, Auditing, Notifications) — and it is reachable on the product's real, public `API_DOMAIN` through Traefik with no rate limit and no authentication. Each unauthenticated hit to `GET /health` triggers roughly half a dozen live checks against Postgres/Redis/Hangfire storage, and the response discloses check names, descriptions and per-check durations — enough to fingerprint the stack's dependency topology to anyone who asks.

**Severity: medium** — not by itself a severe exposure (no secrets, no data), but the combination of unauthenticated, unthrottled, and DB/Redis-touching is an easy amplification lever against the product's own database that a rate limit would close for free. **Fix size: S** — apply a (generous) rate-limit policy to `GET /health` specifically, leaving `/health/live` and `/health/ready` exempt as they are today.

### O11. Every exception-path response is logged at Error, including ordinary 4xx

`src/BuildingBlocks/Web/Exceptions/GlobalExceptionHandler.cs:102` calls `logger.LogError(...)` unconditionally, for every branch: FluentValidation 400s, any `CustomException` (whatever status it carries), `UnauthorizedAccessException` (401), `KeyNotFoundException` (404), and `BadHttpRequestException` (usually 400) — not only the generic 500 branch. A first product's Error-level log volume is therefore dominated by routine client mistakes (bad input, an expired token, a 404) rather than genuine server faults, which both makes the console noisier than it needs to be and — more importantly — undermines any future "alert on Error-level rate" rule, since that rate is mostly client noise rather than an incident signal.

**Severity: medium** — doesn't hide anything by itself, but works against any severity-based triage or alerting a first product tries to layer on top. **Fix size: S** — branch the log level on the resolved `statusCode` (`>= 500` → `LogError`, otherwise `LogWarning` or `LogInformation`).

### O12. No access/request log

No file in the tree calls `app.UseSerilogRequestLogging()` (checked across `src/`), and the ASP.NET Core hosting request-logging middleware's own output is suppressed anyway by the `"Microsoft"` → `Warning` override (`Serilog/Extensions.cs:27`). So there is no per-request line (method, path, status code, duration) on stdout at all, in any environment — the only durable record of "a request happened" is whatever the Auditing module chooses to persist to its own database (a different mechanism, gated by its own config), not a log line an operator can `grep`/`tail` for request volume or latency.

**Severity: low** — request-level tracing (when OTLP export is on, see O5) covers the same ground with more detail; this is a gap only in the always-available stdout view. **Fix size: S** — add `app.UseSerilogRequestLogging()` in the middleware pipeline.

### O13. `ProblemDetails.Title` leaks the raw exception type name

`src/BuildingBlocks/Web/Exceptions/GlobalExceptionHandler.cs:48`: for any `CustomException`, `problemDetails.Title = e.GetType().Name;` — so a caller's error response `Title` is literally the .NET exception class name (e.g. `TenantNotFoundException`), rather than a stable, product-chosen string. Not exploitable on its own (no stack trace, no message beyond what the exception's own `.Message` already intentionally exposes), but it is one more sliver of internal implementation detail in a public response than necessary, and it means renaming an internal exception type is a silent, undocumented breaking change to the API's error shape.

**Severity: low**. **Fix size: S** — give `CustomException` (or its subclasses) an explicit `Title` property instead of deriving one from the CLR type name.

## Checked and sound

- **Trace id and correlation id in every error response:** `GlobalExceptionHandler.cs:89-95` sets `problemDetails.Extensions["traceId"]` from `Activity.Current?.TraceId` (falling back to `HttpContext.TraceIdentifier`) and `["correlationId"]` from the `X-Correlation-ID` request header (falling back to the same). This is exactly the "trace id in the response" the ticket asks about, and it's present on every branch, not just 500s.
- **No stack trace or exception detail ever leaks to the client:** the generic 500 branch (`GlobalExceptionHandler.cs:79-85`) always uses a fixed `Title`/`Detail` regardless of environment; there is no `if (env.IsDevelopment())` branch that swaps in exception details in the production code path. Tests substitute a distinct `DetailedTestExceptionHandler` (`src/Tests/Integration.Tests/Infrastructure/DetailedTestExceptionHandler.cs`) rather than the real handler ever exposing more.
- **Liveness/readiness/full-report are properly split and each is fit for its purpose:** `HealthEndpoints.cs` maps `/health/live` (process-only, no checks run), `/health/ready` (only checks tagged `HealthTags.Ready`), and `/health` (every check, operator-facing). `HealthTags.cs`'s own remarks document *why* readiness stays cheap — one representative `DbContext` check stands in for all five modules' databases (they're the same Postgres server), and Hangfire is deliberately excluded from readiness (`Extensions.cs:97-99`: background processing being down shouldn't take the API out of rotation) — a genuinely reasoned default, not an oversight.
- **Health checks don't pollute tracing:** `OpenTelemetry/Extensions.cs:144,186-188` filters `/health` and `/alive` paths out of ASP.NET Core trace instrumentation, so probe traffic doesn't drown out real request traces once export is on.
- **Readiness is correctly wired into the Dokploy stack's edge:** Traefik's load-balancer healthcheck targets `/health/ready` on a 10s interval with the correct `Host` header set via the `healthcheck.hostname` label (`deploy/dokploy/app.compose.yml:133-141`), which matters because `AllowedHosts` would otherwise 400 the probe — the comment at the same location explains exactly this failure mode. The migrator is also required to complete successfully before the API container starts in both the local and Dokploy compose files.
- **One switch drives logs, traces and metrics consistently:** `AddHeroOpenTelemetry` and `AddHeroLogging` both resolve the same precedence — an injected `OTEL_EXPORTER_OTLP_ENDPOINT` (Aspire) wins, otherwise the configured `Exporter.Otlp` settings apply — and both resolve the same `service.name` (`OTEL_SERVICE_NAME` env var, falling back to `ApplicationName`), so the three signals land under one consistent resource name in whatever backend receives them.
- **Purpose-built meters already exist, ready to receive traffic the moment export is turned on:** caching (hits/misses/factory duration/invalidations, `CachingTelemetry`), the auditing pipeline (published/dropped/flush/flush-failed/dead-lettered/duration), and the outbox (dead-lettered/redriven, see O8 for what's still missing) are all registered with the `MeterProvider` today.
- **PII minimization is the norm elsewhere in the log call sites reviewed:** `TokenGeneratedLogHandler.cs:27`, `UserRegisteredConfirmationMailHandler.cs`, and `PasswordChangedEventHandler.cs` all log `UserId` rather than an email address — the exception is O3's enricher, not the general pattern. No password, JWT, refresh token, or connection-string value was found logged anywhere in the call sites reviewed.
- **Idempotency keys are hashed before logging**, not logged raw.
- **Log forging is guarded against on the one interpolated path checked:** the request path is stripped of newlines before being placed into the error log message (`GlobalExceptionHandler.cs:102`, `.Replace(Environment.NewLine, string.Empty)`).
- **The Hangfire dashboard (Job monitor) sits behind auth and a dedicated permission**, per the separately-decided design in [#93](https://github.com/BeshoyHindy/dotnet-saas-boilerplate/issues/93)/[#102](https://github.com/BeshoyHindy/dotnet-saas-boilerplate/issues/102) — not re-derived here, just confirmed still true of the wiring reviewed.
