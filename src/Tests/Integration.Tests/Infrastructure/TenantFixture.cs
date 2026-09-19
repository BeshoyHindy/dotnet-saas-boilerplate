using System.Net.Http.Json;
using System.Text.Json;
using Finbuckle.MultiTenant;
using Finbuckle.MultiTenant.Abstractions;
using Boilerplate.BuildingBlocks.Shared.Multitenancy;
using Boilerplate.Modules.Identity.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Integration.Tests.Infrastructure;

/// <summary>
/// Provisioning a throwaway tenant (and a user inside it) is the arrange step of every
/// cross-tenant test. These helpers were duplicated per test class; they live here so the
/// impersonation and operator-exchange suites agree on what "a working tenant" means.
/// </summary>
internal static class TenantFixture
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public static async Task CreateTenantAsync(HttpClient rootClient, string tenantId, string adminEmail)
    {
        ArgumentNullException.ThrowIfNull(rootClient);

        var response = await rootClient.PostAsJsonAsync(TestConstants.TenantsBasePath, new
        {
            id = tenantId,
            name = $"Test {tenantId}",
            connectionString = (string?)null,
            adminEmail,
            adminPassword = TestConstants.DefaultPassword,
            issuer = $"{tenantId}.issuer",
        });
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    public static async Task WaitForProvisioningAsync(HttpClient client, string tenantId, int maxRetries = 60)
    {
        ArgumentNullException.ThrowIfNull(client);

        for (var i = 0; i < maxRetries; i++)
        {
            var statusResponse = await client.GetAsync($"{TestConstants.TenantsBasePath}/{tenantId}/provisioning");
            if (statusResponse.IsSuccessStatusCode)
            {
                var content = await statusResponse.Content.ReadAsStringAsync();
                if (content.Contains("Completed", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
                if (content.Contains("Failed", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"Tenant {tenantId} provisioning failed: {content}");
                }
            }
            await Task.Delay(1000);
        }

        throw new TimeoutException($"Tenant {tenantId} did not finish provisioning.");
    }

    /// <summary>
    /// Token issuance for a freshly seeded tenant admin races the seeding step of provisioning —
    /// retry briefly rather than flaking.
    /// </summary>
    public static async Task<TokenResult> GetTokenWithRetryAsync(
        AuthHelper auth, string email, string password, string tenant, int maxRetries = 30)
    {
        ArgumentNullException.ThrowIfNull(auth);

        Exception? last = null;
        for (var i = 0; i < maxRetries; i++)
        {
            try
            {
                return await auth.GetTokenAsync(email, password, tenant);
            }
            catch (HttpRequestException ex)
            {
                last = ex;
                await Task.Delay(500);
            }
        }

        throw last ?? new InvalidOperationException("token issuance failed");
    }

    /// <summary>
    /// Registers a user inside <paramref name="tenantId"/> using an admin client of that tenant and
    /// force-confirms their email (the test host has no SMTP). The user only gets the Basic role,
    /// which makes them a good "lacks permission" subject.
    /// </summary>
    public static async Task<TestUser> RegisterAndConfirmUserAsync(
        AppWebApplicationFactory factory,
        HttpClient adminClient,
        string tenantId,
        string prefix)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(adminClient);

        var unique = Guid.NewGuid().ToString("N")[..8];
        var email = $"{prefix}-{unique}@xchg.com";
        var userName = $"{prefix}{unique}";
        const string password = "Test@1234!";

        using var response = await adminClient.PostAsJsonAsync(
            $"{TestConstants.IdentityBasePath}/register", new
            {
                firstName = prefix,
                lastName = "User",
                email,
                userName,
                password,
                confirmPassword = password,
            });
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var registered = await response.Content.ReadFromJsonAsync<RegisterResult>(Json);

        await ConfirmEmailAsync(factory, tenantId, registered!.UserId);
        return new TestUser(registered.UserId, email, password);
    }

    private static async Task ConfirmEmailAsync(AppWebApplicationFactory factory, string tenantId, string userId)
    {
        using var scope = factory.Services.CreateScope();
        var tenantStore = scope.ServiceProvider.GetRequiredService<IMultiTenantStore<AppTenantInfo>>();
        var tenant = await tenantStore.GetAsync(tenantId);
        scope.ServiceProvider.GetRequiredService<IMultiTenantContextSetter>().MultiTenantContext =
            new MultiTenantContext<AppTenantInfo>(tenant);

        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var user = await userManager.FindByIdAsync(userId);
        user.ShouldNotBeNull();
        if (!user!.EmailConfirmed)
        {
            user.EmailConfirmed = true;
            (await userManager.UpdateAsync(user)).Succeeded.ShouldBeTrue();
        }
    }

    public sealed record TestUser(string UserId, string Email, string Password);

    private sealed class RegisterResult
    {
        public string UserId { get; set; } = default!;
    }
}
