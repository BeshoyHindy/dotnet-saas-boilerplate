using Integration.Middleware.Tests.Infrastructure;

namespace Integration.Middleware.Tests.Tests;

/// <summary>
/// Exercises the three health endpoints against the real check registrations. The split exists
/// because the proxy polls readiness continuously: <c>/health/ready</c> must run only the checks
/// tagged <c>ready</c>, while <c>GET /health</c> keeps the full per-module report for operators.
/// </summary>
[Collection(MiddlewareCollectionDefinition.Name)]
public sealed class HealthEndpointTests
{
    /// <summary>Per-module database checks that exist but are NOT readiness dependencies.</summary>
    private static readonly string[] NonReadinessChecks = ["db:identity", "db:auditing", "db:files", "db:notifications"];

    private readonly MiddlewareWebApplicationFactory _factory;

    public HealthEndpointTests(MiddlewareWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private static async Task<HealthPayload> GetHealthAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        response.StatusCode.ShouldNotBe(HttpStatusCode.NotFound, $"{path} must be mapped");

        var payload = await response.Content.ReadFromJsonAsync<HealthPayload>();
        payload.ShouldNotBeNull();
        return payload;
    }

    #region Happy Path

    [Fact]
    public async Task Ready_Should_RunOnlyTheReadinessTaggedChecks()
    {
        // Arrange
        using var client = _factory.CreateClient();

        // Act
        var payload = await GetHealthAsync(client, "/health/ready");
        var names = payload.Results.Select(r => r.Name).ToList();

        // Assert — the tenant catalog answers the "can we serve?" question for every module that
        // shares the same PostgreSQL server; the per-module checks must not be run here.
        names.ShouldContain("db:multitenancy");
        names.ShouldContain("self");
        foreach (var check in NonReadinessChecks)
        {
            names.ShouldNotContain(check, $"'{check}' is not tagged ready, so readiness must skip it");
        }
    }

    [Fact]
    public async Task Health_Should_ReturnTheFullReport()
    {
        // Arrange
        using var client = _factory.CreateClient();

        // Act
        var payload = await GetHealthAsync(client, "/health");
        var names = payload.Results.Select(r => r.Name).ToList();

        // Assert — everything readiness skips is still reported here.
        names.ShouldContain("db:multitenancy");
        foreach (var check in NonReadinessChecks)
        {
            names.ShouldContain(check);
        }

        names.Count.ShouldBeGreaterThan((await GetHealthAsync(client, "/health/ready")).Results.Count);
    }

    [Fact]
    public async Task Live_Should_RunNoChecks()
    {
        // Arrange
        using var client = _factory.CreateClient();

        // Act
        using var response = await client.GetAsync("/health/live");
        var payload = await response.Content.ReadFromJsonAsync<HealthPayload>();

        // Assert — liveness must answer without touching a dependency.
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        payload.ShouldNotBeNull();
        payload.Results.ShouldBeEmpty();
    }

    #endregion
}
