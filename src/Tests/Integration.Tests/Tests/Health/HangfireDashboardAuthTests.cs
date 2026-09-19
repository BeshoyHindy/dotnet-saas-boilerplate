using Boilerplate.BuildingBlocks.Shared.Constants;
using Boilerplate.BuildingBlocks.Shared.Multitenancy;
using Boilerplate.Modules.Identity.Domain;
using Finbuckle.MultiTenant;
using Finbuckle.MultiTenant.Abstractions;
using Integration.Tests.Infrastructure;
using Microsoft.AspNetCore.Identity;

namespace Integration.Tests.Tests.Health;

/// <summary>
/// The Hangfire dashboard is mounted as a routed endpoint gated by
/// <see cref="SystemPermissions.Hangfire.View"/>, an operator (root-only) permission — it is no
/// longer behind a shared basic-auth credential that every operator copy-pastes and nobody rotates.
/// Authentication and authorization are the platform's, so the dashboard answers 401/403 exactly
/// like every other endpoint.
/// </summary>
[Collection(AppCollectionDefinition.Name)]
public sealed class HangfireDashboardAuthTests
{
    private const string DashboardPath = "/jobs";

    private readonly AppWebApplicationFactory _factory;
    private readonly AuthHelper _auth;

    public HangfireDashboardAuthTests(AppWebApplicationFactory factory)
    {
        _factory = factory;
        _auth = new AuthHelper(factory);
    }

    [Fact]
    public async Task HangfireDashboard_Should_Return401_When_AnonymousRequest()
    {
        using var client = _factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var response = await client.GetAsync(DashboardPath);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task HangfireDashboard_Should_Return403_When_CallerLacksTheOperatorPermission()
    {
        var (email, password) = await CreateRolelessUserAsync($"hf-{Guid.NewGuid().ToString("N")[..8]}");
        using var client = await _auth.CreateAuthenticatedClientAsync(email, password);

        var response = await client.GetAsync(DashboardPath);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden,
            $"Hangfire dashboard must require {SystemPermissions.Hangfire.View}.");
    }

    [Fact]
    public async Task HangfireDashboard_Should_ReturnOk_When_RootOperatorHasThePermission()
    {
        using var client = await _auth.CreateRootAdminClientAsync();

        var response = await client.GetAsync(DashboardPath);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    /// <summary>
    /// Seeds a confirmed, active user with no role assignments — an authenticated caller holding no
    /// permissions at all. The Finbuckle tenant context is set INLINE because it is AsyncLocal.
    /// </summary>
    private async Task<(string Email, string Password)> CreateRolelessUserAsync(string handle)
    {
        const string password = TestConstants.DefaultPassword;
        var email = $"{handle}@example.com";

        using var scope = _factory.Services.CreateScope();

        var tenant = await scope.ServiceProvider
            .GetRequiredService<IMultiTenantStore<AppTenantInfo>>()
            .GetAsync(TestConstants.RootTenantId);
        scope.ServiceProvider.GetRequiredService<IMultiTenantContextSetter>()
            .MultiTenantContext = new MultiTenantContext<AppTenantInfo>(tenant);

        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var user = new AppUser
        {
            FirstName = "Hangfire",
            LastName = "Probe",
            Email = email,
            UserName = handle,
            EmailConfirmed = true,
            IsActive = true,
        };

        var result = await userManager.CreateAsync(user, password);
        result.Succeeded.ShouldBeTrue(
            $"Seeding active user failed: {string.Join(", ", result.Errors.Select(e => e.Description))}");

        return (email, password);
    }
}
