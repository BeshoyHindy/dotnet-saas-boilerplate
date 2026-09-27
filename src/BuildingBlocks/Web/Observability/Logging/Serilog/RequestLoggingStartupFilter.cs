using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Serilog;
using Serilog.Events;

namespace Boilerplate.BuildingBlocks.Web.Observability.Logging.Serilog;

/// <summary>
/// Puts Serilog's request logging in front of everything the app itself configures, so each request
/// leaves one line — method, path, status, duration — carrying the same enrichment (tenant, user,
/// correlation id) as every other event of that request.
/// </summary>
/// <remarks>
/// Level: Error for a 5xx; Verbose (below every shipped minimum, so dropped) for a successful
/// liveness or readiness probe, which the proxy polls every few seconds; Information otherwise.
/// </remarks>
internal sealed class RequestLoggingStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
        app =>
        {
            app.UseSerilogRequestLogging(options => options.GetLevel = GetLevel);
            next(app);
        };

    internal static LogEventLevel GetLevel(HttpContext context, double elapsedMs, Exception? exception)
    {
        if (exception is not null || context.Response.StatusCode >= StatusCodes.Status500InternalServerError)
        {
            return LogEventLevel.Error;
        }

        return IsProbe(context.Request.Path) ? LogEventLevel.Verbose : LogEventLevel.Information;
    }

    private static bool IsProbe(PathString path) =>
        path.Equals("/health/live", StringComparison.OrdinalIgnoreCase) ||
        path.Equals("/health/ready", StringComparison.OrdinalIgnoreCase);
}
