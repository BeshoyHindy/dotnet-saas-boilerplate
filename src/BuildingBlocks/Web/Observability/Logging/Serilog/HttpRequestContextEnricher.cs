using Boilerplate.BuildingBlocks.Shared.Identity.Claims;
using Microsoft.AspNetCore.Http;
using Serilog.Core;
using Serilog.Events;

namespace Boilerplate.BuildingBlocks.Web.Observability.Logging.Serilog;

public class HttpRequestContextEnricher : ILogEventEnricher
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public HttpRequestContextEnricher(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        ArgumentNullException.ThrowIfNull(propertyFactory);

        // Get HttpContext properties here
        var httpContext = _httpContextAccessor.HttpContext;

        if (httpContext != null)
        {
            // Add properties to the log event based on HttpContext
            logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty("RequestMethod", httpContext.Request.Method));
            logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty("RequestPath", httpContext.Request.Path));
            logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty("UserAgent", httpContext.Request.Headers["User-Agent"]));

            // Id and tenant identify the caller for correlation. No personal field (the email address)
            // is added: this runs for every event of the request, so it would reach every log line and
            // every backend the logs are shipped to.
            if (httpContext.User?.Identity?.IsAuthenticated == true)
            {
                logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty("UserId", httpContext.User.GetUserId()));
                logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty("Tenant", httpContext.User.GetTenant()));
            }
        }
    }
}