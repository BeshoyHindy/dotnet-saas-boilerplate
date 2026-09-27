using System.Security.Claims;
using Boilerplate.BuildingBlocks.Shared.Constants;
using Boilerplate.BuildingBlocks.Web.Observability.Logging.Serilog;
using Microsoft.AspNetCore.Http;
using Serilog.Core;
using Serilog.Events;
using Serilog.Parsing;

namespace Framework.Tests.Web.Logging;

/// <summary>
/// The enricher stamps every event raised while a request is handled, so whatever it adds reaches
/// every log line and every backend logs are shipped to. It identifies the caller by id and tenant,
/// never by a personal field such as the email address.
/// </summary>
public sealed class HttpRequestContextEnricherTests
{
    private static LogEvent Enrich(HttpContext context)
    {
        var accessor = new HttpContextAccessor { HttpContext = context };
        var enricher = new HttpRequestContextEnricher(accessor);
        var logEvent = new LogEvent(
            DateTimeOffset.UtcNow, LogEventLevel.Information, null,
            new MessageTemplateParser().Parse("test"), []);

        enricher.Enrich(logEvent, new ScalarPropertyFactory());
        return logEvent;
    }

    private static DefaultHttpContext AuthenticatedContext()
    {
        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, "user-42"),
                new Claim(CustomClaims.Tenant, "acme"),
                new Claim(ClaimTypes.Email, "someone@example.com"),
            ],
            authenticationType: "test");

        var context = new DefaultHttpContext { User = new ClaimsPrincipal(identity) };
        context.Request.Method = "GET";
        context.Request.Path = "/api/v1/things";
        return context;
    }

    [Fact]
    public void Enrich_Should_IdentifyTheCallerByIdAndTenant_When_Authenticated()
    {
        var logEvent = Enrich(AuthenticatedContext());

        logEvent.Properties["UserId"].ToString().ShouldBe("\"user-42\"");
        logEvent.Properties["Tenant"].ToString().ShouldBe("\"acme\"");
    }

    [Fact]
    public void Enrich_Should_NeverAddTheEmailAddress_When_Authenticated()
    {
        var logEvent = Enrich(AuthenticatedContext());

        logEvent.Properties.Keys.ShouldNotContain("UserEmail");
        logEvent.Properties.Values
            .Select(v => v.ToString())
            .ShouldNotContain(v => v.Contains("someone@example.com", StringComparison.Ordinal));
    }

    private sealed class ScalarPropertyFactory : ILogEventPropertyFactory
    {
        public LogEventProperty CreateProperty(string name, object? value, bool destructureObjects = false) =>
            new(name, new ScalarValue(value?.ToString()));
    }
}
