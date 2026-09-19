using Integration.Tests.Infrastructure;

namespace Integration.Tests.Tests.Multitenancy;

/// <summary>
/// The anonymous, tenant-scoped endpoints exist only under <c>/api/v1/tenants/{tenant}/auth/...</c>
/// (ADR-0002). Their old un-tenanted <c>/api/v1/identity/...</c> forms — which took the tenant from a
/// header and returned 400 when it was absent (issue #1245) — are deleted, not redirected: no route
/// is left that could resolve a tenant from anything the caller chooses to send.
/// </summary>
[Collection(AppCollectionDefinition.Name)]
public sealed class MissingTenantTests
{
    private readonly AppWebApplicationFactory _factory;

    public MissingTenantTests(AppWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task IssueToken_Should_Return404_When_CalledOnTheRetiredHeaderRoute()
    {
        using var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            $"{TestConstants.IdentityBasePath}/token/issue",
            new { email = TestConstants.RootAdminEmail, password = TestConstants.DefaultPassword });

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task RefreshToken_Should_Return404_When_CalledOnTheRetiredHeaderRoute()
    {
        using var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            $"{TestConstants.IdentityBasePath}/token/refresh",
            new { token = "x", refreshToken = "y" });

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ForgotPassword_Should_Return404_When_CalledOnTheRetiredHeaderRoute()
    {
        using var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            $"{TestConstants.IdentityBasePath}/forgot-password",
            new { email = "nobody@example.com" });

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ResetPassword_Should_Return404_When_CalledOnTheRetiredHeaderRoute()
    {
        using var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            $"{TestConstants.IdentityBasePath}/reset-password",
            new { email = "nobody@example.com", token = "x", password = "Test@1234!" });

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task SelfRegister_Should_Return404_When_CalledOnTheRetiredHeaderRoute()
    {
        using var client = _factory.CreateClient();
        var uniqueId = Guid.NewGuid().ToString("N")[..8];

        var response = await client.PostAsJsonAsync(
            $"{TestConstants.IdentityBasePath}/self-register",
            new
            {
                firstName = "Self",
                lastName = "Reg",
                email = $"self-{uniqueId}@example.com",
                userName = $"selfreg-{uniqueId}",
                password = "Test@1234!",
                confirmPassword = "Test@1234!"
            });

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
