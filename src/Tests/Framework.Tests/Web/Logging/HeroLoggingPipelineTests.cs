using Boilerplate.BuildingBlocks.Core.Exceptions;
using Boilerplate.BuildingBlocks.Web.Exceptions;
using Boilerplate.BuildingBlocks.Web.Observability.Logging.Serilog;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog.Events;

namespace Framework.Tests.Web.Logging;

/// <summary>
/// Drives a real (in-memory) host wired by <c>AddHeroLogging</c> and reads what reaches the sink.
/// Every test here lives in one class on purpose: Serilog's hosting integration routes through the
/// static <c>Log.Logger</c>, so two hosts built in parallel would write into each other's logger.
/// </summary>
public sealed class HeroLoggingPipelineTests
{
    private const string RequestLoggingSource = "Serilog.AspNetCore.RequestLoggingMiddleware";

    private static async Task<WebApplication> StartHostAsync(
        Action<WebApplication> map,
        IDictionary<string, string?>? extraConfig = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseTestServer();

        var config = new Dictionary<string, string?>
        {
            ["Serilog:Using:0"] = "Boilerplate.Framework.Tests",
            ["Serilog:WriteTo:0:Name"] = "Capture",
            ["Serilog:MinimumLevel:Default"] = "Information",
            ["OpenTelemetryOptions:Enabled"] = "false",
        };
        foreach (var (key, value) in extraConfig ?? new Dictionary<string, string?>())
        {
            config[key] = value;
        }

        builder.Configuration.AddInMemoryCollection(config);
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddProblemDetails();
        builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
        builder.AddHeroLogging();

        var app = builder.Build();
        app.UseExceptionHandler();
        map(app);
        await app.StartAsync();
        return app;
    }

    private static List<LogEvent> EventsFor(string path) =>
        CaptureSink.Instance.Events
            .Where(e => e.Properties.TryGetValue("RequestPath", out var p) && p.ToString().Contains(path, StringComparison.Ordinal))
            .ToList();

    private static List<LogEvent> RequestLogFor(string path) =>
        EventsFor(path)
            .Where(e => e.Properties.TryGetValue("SourceContext", out var s) && s.ToString() == $"\"{RequestLoggingSource}\"")
            .ToList();

    private static int StatusCodeOf(LogEvent logEvent) =>
        int.Parse(logEvent.Properties["StatusCode"].ToString(), System.Globalization.CultureInfo.InvariantCulture);

    // O12: every request leaves one access-log line with its method, path, status and duration.
    [Fact]
    public async Task Request_Should_WriteOneAccessLogLine_When_Served()
    {
        var path = $"/ok-{Guid.NewGuid():N}";
        await using var app = await StartHostAsync(a => a.MapGet(path, () => Results.Ok()));

        using var response = await app.GetTestClient().GetAsync(path);

        var line = RequestLogFor(path).ShouldHaveSingleItem();
        line.Level.ShouldBe(LogEventLevel.Information);
        StatusCodeOf(line).ShouldBe(StatusCodes.Status200OK);
        line.Properties.ShouldContainKey("Elapsed");
    }

    // The access log wraps the exception handler, so it records the status the caller received.
    [Fact]
    public async Task Request_Should_LogTheHandledStatus_When_AnEndpointThrowsAClientError()
    {
        var path = $"/missing-{Guid.NewGuid():N}";
        await using var app = await StartHostAsync(a => a.MapGet(path, (Func<IResult>)(() => throw new NotFoundException("gone"))));

        using var response = await app.GetTestClient().GetAsync(path);

        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.NotFound);
        StatusCodeOf(RequestLogFor(path).ShouldHaveSingleItem()).ShouldBe(StatusCodes.Status404NotFound);
    }

    // O1 end to end: the unhandled-error line that reaches the sink carries the exception itself.
    [Fact]
    public async Task Request_Should_LogTheExceptionAtError_When_AnEndpointFails()
    {
        var path = $"/boom-{Guid.NewGuid():N}";
        await using var app = await StartHostAsync(a => a.MapGet(path, (Func<IResult>)(() => throw new InvalidOperationException("kaput"))));

        using var response = await app.GetTestClient().GetAsync(path);

        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.InternalServerError);
        var errorLine = EventsFor(path)
            .Where(e => e.Properties.TryGetValue("SourceContext", out var s) && s.ToString().Contains(nameof(GlobalExceptionHandler), StringComparison.Ordinal))
            .ShouldHaveSingleItem();
        errorLine.Level.ShouldBe(LogEventLevel.Error);
        errorLine.Exception.ShouldBeOfType<InvalidOperationException>().Message.ShouldBe("kaput");

        var accessLine = RequestLogFor(path).ShouldHaveSingleItem();
        accessLine.Level.ShouldBe(LogEventLevel.Error);
        StatusCodeOf(accessLine).ShouldBe(StatusCodes.Status500InternalServerError);
    }

    // The proxy polls readiness every few seconds; a healthy probe must not flood the access log.
    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task Request_Should_NotWriteAnAccessLogLine_When_AProbeSucceeds(string probe)
    {
        await using var app = await StartHostAsync(a => a.MapGet(probe, () => Results.Ok()));
        var before = RequestLogFor(probe).Count;

        using var response = await app.GetTestClient().GetAsync(probe);

        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
        RequestLogFor(probe).Count.ShouldBe(before);
    }

    // O4: category levels come from Serilog:MinimumLevel:Override, not from code the deployment cannot change.
    [Fact]
    public async Task CategoryLevel_Should_FollowConfiguration_When_TheDeploymentRaisesIt()
    {
        var marker = Guid.NewGuid().ToString("N");
        await using var app = await StartHostAsync(
            _ => { },
            new Dictionary<string, string?>
            {
                ["Serilog:MinimumLevel:Override:Microsoft.EntityFrameworkCore"] = "Information",
            });

        app.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger("Microsoft.EntityFrameworkCore.Database.Command")
            .LogInformation("Executed command {Marker}", marker);

        CaptureSink.Instance.Events
            .ShouldContain(e => e.Properties.ContainsKey("Marker") && e.Properties["Marker"].ToString().Contains(marker, StringComparison.Ordinal));
    }
}
