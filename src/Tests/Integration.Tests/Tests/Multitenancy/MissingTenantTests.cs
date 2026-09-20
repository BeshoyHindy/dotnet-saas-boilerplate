using Integration.Tests.Infrastructure;
using Microsoft.AspNetCore.Routing;

namespace Integration.Tests.Tests.Multitenancy;

/// <summary>
/// The anonymous, tenant-scoped endpoints exist only under <c>/api/v1/tenants/{tenant}/auth/...</c>
/// (ADR-0002). Their old un-tenanted <c>/api/v1/identity/...</c> forms — which took the tenant from a
/// header and returned 400 when it was absent (issue #1245) — are deleted, not redirected: no route
/// is left that could resolve a tenant from anything the caller chooses to send.
///
/// Asserted twice over, because the two facts differ. The routing table must not contain the retired
/// patterns at all; and over HTTP those paths must answer like any other unmapped route. They answer
/// 404 (not 401): the host's catch-all fallback endpoint (issue #47) intercepts requests matching no
/// endpoint before the <c>FallbackPolicy</c> authorization policy would otherwise turn them into a
/// 401 — unrelated to tenant resolution.
/// </summary>
[Collection(AppCollectionDefinition.Name)]
public sealed class MissingTenantTests
{
    /// <summary>The route suffixes that used to take the tenant from a header.</summary>
    private static readonly string[] RetiredRouteSuffixes =
    {
        "identity/token/issue",
        "identity/token/refresh",
        "identity/forgot-password",
        "identity/reset-password",
        "identity/confirm-email",
        "identity/self-register",
    };

    private readonly AppWebApplicationFactory _factory;

    public MissingTenantTests(AppWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public void RetiredHeaderRoutes_Should_NotBeRegistered()
    {
        _ = _factory.Server;

        var patterns = _factory.Services
            .GetRequiredService<EndpointDataSource>()
            .Endpoints
            .OfType<RouteEndpoint>()
            .Select(e => e.RoutePattern.RawText)
            .Where(raw => raw is not null)
            .ToList();

        var survivors = RetiredRouteSuffixes
            .Where(suffix => patterns.Exists(
                raw => raw!.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        survivors.ShouldBeEmpty(
            "ADR-0002 moved every anonymous tenant-scoped endpoint under " +
            "api/v1/tenants/{tenant}/auth/. These header-era routes must not exist:\n  - " +
            string.Join("\n  - ", survivors));
    }

    [Theory]
    [InlineData("/api/v1/identity/token/issue")]
    [InlineData("/api/v1/identity/token/refresh")]
    [InlineData("/api/v1/identity/forgot-password")]
    [InlineData("/api/v1/identity/reset-password")]
    [InlineData("/api/v1/identity/self-register")]
    public async Task RetiredHeaderRoutes_Should_Return404(string path)
    {
        using var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            path,
            new { email = TestConstants.RootAdminEmail, password = TestConstants.DefaultPassword });

        response.IsSuccessStatusCode.ShouldBeFalse();
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
