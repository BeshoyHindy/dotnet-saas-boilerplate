# Logging & observability

`src/BuildingBlocks/Web/Observability/`. Read before adding logs, traces, or metrics.

## Structured logging only

**No string interpolation in log messages.** Use message templates with named placeholders, or `[LoggerMessage]` source-gen for hot paths.

```csharp
// good
_logger.LogInformation("Cleaned up {Count} expired sessions for tenant {TenantId}", count, tenantId);
// also good (hot path) — see OutboxDispatcher, InMemoryEventBus
[LoggerMessage(Level = LogLevel.Warning, Message = "Outbox message {MessageId} dead-lettered")]
private partial void LogDeadLettered(Guid messageId);
// NEVER
_logger.LogInformation($"Cleaned up {count} sessions");   // breaks structured logging + analyzers
```

Build runs with `TreatWarningsAsErrors` — interpolated log calls won't even compile clean under analysis.

## Serilog

`AddAppLogging()` reads the `Serilog` config section (Console sink only), attaches `HttpRequestContextEnricher` (adds `RequestMethod`/`RequestPath`/`UserAgent` + `UserId`/`Tenant` when authenticated — never the email address or any other personal field: the enricher stamps every event of the request), and excludes the `ExceptionHandlerMiddleware` source (the global handler logs exceptions itself — don't double-log).

- **Levels live in config.** Category overrides (Microsoft, EF Core, Hangfire, Finbuckle) are in `Serilog:MinimumLevel:Override` in the API's `appsettings.json`; never set one in code, where it would silently beat the deployment's config. There is no `Logging:LogLevel` section: Serilog replaces the Microsoft.Extensions.Logging providers, so it would do nothing.
- **Rendering.** Development's Console `outputTemplate` prints tenant, user and correlation id; Production's Console uses `RenderedCompactJsonFormatter`, one JSON object per event with every property. A new enriched property is invisible in Development until the template names it.
- **Access log.** `AddAppLogging` registers a startup filter that puts `UseSerilogRequestLogging` outside the whole pipeline, so the line records the status the caller received. 5xx → Error; a successful `/health/live` or `/health/ready` probe → Verbose (dropped); otherwise Information.
- **Exceptions.** `GlobalExceptionHandler` logs a ≥500 at Error with the exception attached, and a 4xx at Warning with the exception type and detail but no stack trace. A `CustomException`'s ProblemDetails `Title` is the status reason phrase, never the CLR type name.
- **No static sink entries for OTLP** in `Serilog:WriteTo` — log export is added in code only when an endpoint resolves (below).

## Correlation

`X-Correlation-ID` request header (falls back to `HttpContext.TraceIdentifier`), surfaced in every ProblemDetails. Log events carry a per-request `CorrelationId` from the `WithCorrelationId` enricher (Serilog.Enrichers.CorrelationId). `CurrentUserMiddleware` tags the current `Activity` with `boilerplate.user_id` / `boilerplate.tenant_id` / `boilerplate.correlation_id`.

## OpenTelemetry

`AddAppOpenTelemetry()` no-ops unless `OpenTelemetryOptions.Enabled`. Metrics + traces for AspNetCore/HttpClient/Npgsql/EFCore/Redis/Runtime, plus caching + auditing meters and Mediator pipeline spans (`MediatorTracingBehavior`). Add a new meter/source name to `OpenTelemetryOptions` config, not by editing the extension.

**OTLP export is auto-detected.** It turns on when **either** `Exporter.Otlp.Enabled=true` **or** the `OTEL_EXPORTER_OTLP_ENDPOINT` env var is present. Under .NET Aspire that env var is injected automatically, so traces/metrics flow to the Aspire dashboard with no config change (the SDK reads endpoint + protocol from the standard `OTEL_EXPORTER_OTLP_*` env vars; the config `Endpoint`/`Protocol` are only used when no env var is set — e.g. the docker-compose collector at `http://localhost:4317`). Plain `dotnet run` with no collector and `Enabled=false` exports nothing.

**Logs ride the same OTLP detection.** Serilog owns the logging pipeline and does not forward to other `ILogger` providers, so the OTel SDK's log exporter can't see Serilog events — instead `AddAppLogging` adds a `Serilog.Sinks.OpenTelemetry` sink under the same auto-detect rule (global `Enabled` + env-var-or-config endpoint). It also parses `OTEL_EXPORTER_OTLP_HEADERS` (the Aspire dashboard's OTLP receiver requires its `x-otlp-api-key`; the SDK reads this for traces/metrics, the Serilog sink does not) and stamps `service.name` = `OTEL_SERVICE_NAME ?? ApplicationName` so logs group under the same dashboard resource as the spans. Both `AddAppOpenTelemetry` and the Serilog sink resolve the name that way: under an orchestrator that injects `OTEL_SERVICE_NAME` (Aspire sets it to the resource name, e.g. `boilerplate-api`) the process adopts that identity; hardcoding the entry-assembly name (`Boilerplate.Api`) instead would de-correlate the telemetry and list the process twice in the dashboard. Plain `dotnet run` with no env var falls back to `ApplicationName`.
