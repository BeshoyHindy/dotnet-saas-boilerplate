using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Filters;
using Serilog.Sinks.OpenTelemetry;

namespace Boilerplate.BuildingBlocks.Web.Observability.Logging.Serilog;

public static class Extensions
{
    public static IHostApplicationBuilder AddAppLogging(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddSingleton<HttpRequestContextEnricher>();

        // One access-log line per request. Registered as a startup filter so it wraps the whole
        // pipeline, outside UseExceptionHandler: the line then records the status the caller actually
        // received (a handled NotFoundException logs 404, not the 500 it would see from inside).
        builder.Services.AddTransient<IStartupFilter, RequestLoggingStartupFilter>();

        // Resolve OTLP log export once (env-var/config), so the sink is only added when an endpoint is available.
        var otlp = ResolveOtlpLogExport(builder);

        builder.Services.AddSerilog((context, logger) =>
        {
            var httpEnricher = context.GetRequiredService<HttpRequestContextEnricher>();
            logger.ReadFrom.Configuration(builder.Configuration);
            logger.Enrich.With(httpEnricher);

            // Category levels (Microsoft, EF Core, Hangfire, Finbuckle) live in Serilog:MinimumLevel:Override
            // in appsettings, not here: an override set in code would silently beat whatever the deployment
            // configures. The one rule that stays in code is structural — the global handler logs every
            // exception itself, so the framework's own copy of the same exception is dropped.
            logger.Filter.ByExcluding(Matching.FromSource("Microsoft.AspNetCore.Diagnostics.ExceptionHandlerMiddleware"));

            // Ship structured logs over OTLP (e.g. the .NET Aspire dashboard / compose collector) when an endpoint is
            // available. Serilog owns the logging pipeline and does NOT forward to other ILogger providers, so the
            // OpenTelemetry SDK's log exporter can't see these events — we export from inside Serilog instead. Mirrors
            // the traces/metrics auto-detect in AddAppOpenTelemetry: an injected OTEL_EXPORTER_OTLP_ENDPOINT (Aspire)
            // wins, otherwise the configured exporter endpoint is used when Exporter.Otlp.Enabled is true.
            if (otlp is not null)
            {
                logger.WriteTo.OpenTelemetry(sink =>
                {
                    sink.Endpoint = otlp.Endpoint;
                    sink.Protocol = otlp.Protocol;
                    // service.name must match the traces/metrics resource (AddAppOpenTelemetry resolves the same
                    // OTEL_SERVICE_NAME ?? ApplicationName) so the dashboard groups logs under the same resource as
                    // the spans they belong to — and adopts the orchestrator's resource name (e.g. Aspire's
                    // "boilerplate-api") rather than the entry-assembly name, which would list the process twice.
                    sink.ResourceAttributes = new Dictionary<string, object>
                    {
                        ["service.name"] = Environment.GetEnvironmentVariable("OTEL_SERVICE_NAME")
                            ?? builder.Environment.ApplicationName
                    };
                    // Aspire's OTLP receiver requires the injected x-otlp-api-key header. The OTel SDK reads
                    // OTEL_EXPORTER_OTLP_HEADERS automatically for traces/metrics; this sink does not, so pass it through.
                    if (otlp.Headers.Count > 0)
                    {
                        sink.Headers = otlp.Headers;
                    }
                });
            }
        });
        return builder;
    }

    private sealed record OtlpLogExport(string Endpoint, OtlpProtocol Protocol, IDictionary<string, string> Headers);

    private static OtlpLogExport? ResolveOtlpLogExport(IHostApplicationBuilder builder)
    {
        // Honor the global OpenTelemetry switch, matching the traces/metrics gate.
        if (!builder.Configuration.GetValue("OpenTelemetryOptions:Enabled", true))
        {
            return null;
        }

        var envEndpoint = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");

        string? endpoint;
        string? protocolRaw;
        if (!string.IsNullOrWhiteSpace(envEndpoint))
        {
            // An injected endpoint (Aspire / collector) takes precedence and exports even if config has Otlp disabled.
            endpoint = envEndpoint;
            protocolRaw = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_PROTOCOL");
        }
        else if (builder.Configuration.GetValue("OpenTelemetryOptions:Exporter:Otlp:Enabled", false))
        {
            endpoint = builder.Configuration["OpenTelemetryOptions:Exporter:Otlp:Endpoint"];
            protocolRaw = builder.Configuration["OpenTelemetryOptions:Exporter:Otlp:Protocol"];
        }
        else
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return null;
        }

        var protocol = protocolRaw?.Trim().ToLowerInvariant() switch
        {
            "http/protobuf" => OtlpProtocol.HttpProtobuf,
            _ => OtlpProtocol.Grpc
        };

        // gRPC uses the base endpoint as-is; for HTTP the Serilog sink expects the full signal path.
        if (protocol == OtlpProtocol.HttpProtobuf &&
            !endpoint.Contains("/v1/logs", StringComparison.OrdinalIgnoreCase))
        {
            endpoint = $"{endpoint.TrimEnd('/')}/v1/logs";
        }

        var headers = ParseOtlpHeaders(Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_HEADERS"));
        return new OtlpLogExport(endpoint, protocol, headers);
    }

    // Parses the comma-separated key/value form used by OTEL_EXPORTER_OTLP_HEADERS.
    private static Dictionary<string, string> ParseOtlpHeaders(string? raw)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return headers;
        }

        foreach (var pair in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var idx = pair.IndexOf('=', StringComparison.Ordinal);
            if (idx <= 0)
            {
                continue;
            }

            var key = pair[..idx].Trim();
            if (key.Length > 0)
            {
                headers[key] = pair[(idx + 1)..].Trim();
            }
        }

        return headers;
    }
}
