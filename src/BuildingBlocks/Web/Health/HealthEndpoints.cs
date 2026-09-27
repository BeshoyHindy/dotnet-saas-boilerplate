using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Boilerplate.BuildingBlocks.Web.Health;

public static class HealthEndpoints
{
    public sealed record HealthResult(string Status, IEnumerable<HealthEntry> Results);
    public sealed record HealthEntry(string Name, string Status, string? Description, double DurationMs, Dictionary<string, object>? Details = default);
    public static IEndpointRouteBuilder MapAppHealthEndpoints(this IEndpointRouteBuilder app)
    {
        // Anonymous by design. Rate limiting is decided per endpoint. The two probes are exempt because
        // the proxy and orchestrator poll them, and a throttled probe would take the API out of rotation.
        // The full report is throttled because each call runs every check against the database, the
        // cache and job storage. Throttling applies only while RateLimitingOptions.Enabled is on.
        var group = app.MapGroup("/health")
                       .WithTags("Health")
                       .AllowAnonymous();

        // Liveness: only process up (no external deps)
        group.MapGet("/live",
                async Task<Ok<HealthResult>> (HealthCheckService hc, CancellationToken cancellationToken) =>
                {
                    var report = await hc.CheckHealthAsync(_ => false, cancellationToken);
                    var payload = new HealthResult(
                    Status: report.Status.ToString(),
                    Results: Array.Empty<HealthEntry>());

                    return TypedResults.Ok(payload);
                })
                .DisableRateLimiting()
                .WithName("Liveness")
                .WithSummary("Quick process liveness probe.")
                .WithDescription("Reports if the API process is alive. Does not check dependencies.")
                .Produces<HealthResult>(StatusCodes.Status200OK);

        // Readiness: only the checks tagged HealthTags.Ready. The proxy polls this on a short
        // interval, so running every per-module check here would open one connection per module per
        // probe against the same database. Full payload on both 200 and 503 so operators see which
        // check failed; probe consumers key off status code, so a 503 body is safe.
        group.MapGet("/ready",
                    async (HealthCheckService hc, CancellationToken cancellationToken) =>
                    {
                        var report = await hc.CheckHealthAsync(
                            registration => registration.Tags.Contains(HealthTags.Ready),
                            cancellationToken);

                        return WriteReport(report);
                    })
                    .DisableRateLimiting()
                    .WithName("Readiness")
                    .WithSummary("Readiness probe over the checks tagged 'ready'.")
                    .WithDescription("Returns 200 if every readiness dependency is healthy, otherwise 503. Body is the same shape in both cases.")
                    .Produces<HealthResult>(StatusCodes.Status200OK)
                    .Produces<HealthResult>(StatusCodes.Status503ServiceUnavailable);

        // Full report: every registered check, including the per-module ones readiness skips.
        // Operator-facing and on demand, not a probe target.
        group.MapGet("/",
                    async (HealthCheckService hc, CancellationToken cancellationToken) =>
                    {
                        var report = await hc.CheckHealthAsync(cancellationToken);
                        return WriteReport(report);
                    })
                    .RequireRateLimiting(new HealthReportRateLimiterPolicy())
                    .WithName("HealthReport")
                    .WithSummary("Full health report across every registered check.")
                    .WithDescription("Runs all checks, including per-module database checks that readiness skips. Returns 200 when healthy, otherwise 503.")
                    .Produces<HealthResult>(StatusCodes.Status200OK)
                    .Produces<HealthResult>(StatusCodes.Status503ServiceUnavailable);

        return app;
    }

    /// <summary>
    /// Per client IP, fixed window. Generous for a person or an uptime monitor, far too tight to
    /// use the report as an amplifier against the database. Rejections share the global 429 handler.
    /// </summary>
    private sealed class HealthReportRateLimiterPolicy : IRateLimiterPolicy<string>
    {
        private const int PermitsPerWindow = 30;
        private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

        public Func<OnRejectedContext, CancellationToken, ValueTask>? OnRejected => null;

        public RateLimitPartition<string> GetPartition(HttpContext httpContext) =>
            RateLimitPartition.GetFixedWindowLimiter(
                $"health-report:{httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown"}",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = PermitsPerWindow,
                    Window = Window,
                    QueueLimit = 0,
                });
    }

    private static IResult WriteReport(HealthReport report)
    {
        var results = report.Entries.Select(e =>
            new HealthEntry(
                Name: e.Key,
                Status: e.Value.Status.ToString(),
                Description: e.Value.Description,
                DurationMs: e.Value.Duration.TotalMilliseconds,
                Details: e.Value.Data.ToDictionary(
                    k => k.Key,
                    v => v.Value is null ? "null" : v.Value
                )));

        var payload = new HealthResult(report.Status.ToString(), results);
        var statusCode = report.Status == HealthStatus.Healthy
            ? StatusCodes.Status200OK
            : StatusCodes.Status503ServiceUnavailable;

        return Results.Json(payload, statusCode: statusCode);
    }
}