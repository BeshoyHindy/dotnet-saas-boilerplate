using System.Net;
using Boilerplate.BuildingBlocks.Web.Health;
using Boilerplate.BuildingBlocks.Web.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Framework.Tests.Web;

/// <summary>
/// <c>GET /health</c> runs every registered check (database, cache, job storage) for an anonymous
/// caller, so it is throttled; the probes the proxy and orchestrator poll — <c>/health/live</c> and
/// <c>/health/ready</c> — must never be, or a busy probe would take the API out of rotation.
/// </summary>
public sealed class HealthEndpointsRateLimitTests
{
    private const int ComfortablyMoreThanAnyLimit = 200;

    private static async Task<WebApplication> StartHostAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["RateLimitingOptions:Enabled"] = "true",
        });

        builder.Services.AddHealthChecks();
        builder.Services.AddHeroRateLimiting(builder.Configuration);

        var app = builder.Build();
        app.UseRouting();
        app.UseHeroRateLimiting();
        app.MapHeroHealthEndpoints();
        await app.StartAsync();
        return app;
    }

    private static async Task<List<HttpStatusCode>> HitAsync(HttpClient client, string path, int times)
    {
        var statuses = new List<HttpStatusCode>(times);
        for (var i = 0; i < times; i++)
        {
            using var response = await client.GetAsync(new Uri(path, UriKind.Relative));
            statuses.Add(response.StatusCode);
        }

        return statuses;
    }

    [Fact]
    public async Task HealthReport_Should_BeRateLimited_When_CalledRepeatedly()
    {
        await using var app = await StartHostAsync();

        var statuses = await HitAsync(app.GetTestClient(), "/health", ComfortablyMoreThanAnyLimit);

        statuses[0].ShouldBe(HttpStatusCode.OK);
        statuses.ShouldContain(HttpStatusCode.TooManyRequests);
    }

    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task Probe_Should_NeverBeRateLimited(string probe)
    {
        await using var app = await StartHostAsync();

        var statuses = await HitAsync(app.GetTestClient(), probe, ComfortablyMoreThanAnyLimit);

        statuses.ShouldAllBe(s => s == HttpStatusCode.OK);
    }
}
