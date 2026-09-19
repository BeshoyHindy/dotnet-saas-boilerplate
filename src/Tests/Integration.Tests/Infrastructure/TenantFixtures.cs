using Boilerplate.Modules.Multitenancy.Contracts.Dtos;

namespace Integration.Tests.Infrastructure;

/// <summary>
/// Creates real, fully provisioned tenants through the operator API — the same path production
/// uses, including the provisioning job — so a test asserting on tenant isolation is asserting
/// about tenants that were made the way tenants are made.
/// </summary>
public sealed class TenantFixtures
{
    private static readonly TimeSpan ProvisioningTimeout = TimeSpan.FromMinutes(2);

    private readonly AuthHelper _auth;

    public TenantFixtures(AppWebApplicationFactory factory) => _auth = new AuthHelper(factory);

    /// <summary>
    /// Creates a tenant with <paramref name="prefix"/> in its id, waits for provisioning to
    /// complete, and returns its id plus the seeded admin's e-mail — a row that exists in that
    /// tenant and nowhere else, which is what isolation assertions key off.
    /// </summary>
    /// <param name="prefix">Prefix for the generated tenant id and admin e-mail.</param>
    /// <param name="connectionString">
    /// A dedicated database for the tenant, or null to share the default one.
    /// </param>
    public async Task<(string TenantId, string AdminEmail)> CreateProvisionedTenantAsync(
        string prefix, string? connectionString = null)
    {
        var unique = Guid.NewGuid().ToString("N")[..8];
        var tenantId = $"{prefix}-{unique}";
        var adminEmail = $"{prefix}-{unique}@tenant.test";

        using var rootClient = await _auth.CreateRootAdminClientAsync();

        var response = await rootClient.PostAsJsonAsync(TestConstants.TenantsBasePath, new
        {
            id = tenantId,
            name = $"Tenant {tenantId}",
            connectionString,
            adminEmail,
            adminPassword = TestConstants.DefaultPassword,
            issuer = $"{tenantId}.issuer",
        });

        response.StatusCode.ShouldBe(
            HttpStatusCode.Created,
            $"creating tenant {tenantId} failed: {await response.Content.ReadAsStringAsync()}");

        await WaitForProvisioningAsync(rootClient, tenantId);

        return (tenantId, adminEmail);
    }

    private static async Task WaitForProvisioningAsync(HttpClient rootClient, string tenantId)
    {
        var deadline = DateTime.UtcNow + ProvisioningTimeout;
        string lastStatus = "unknown";

        while (DateTime.UtcNow < deadline)
        {
            using var response = await rootClient.GetAsync(
                $"{TestConstants.TenantsBasePath}/{tenantId}/provisioning");

            if (response.StatusCode == HttpStatusCode.OK)
            {
                var status = await response.Content.ReadFromJsonAsync<TenantProvisioningStatusDto>();
                lastStatus = status?.Status ?? "unknown";

                if (string.Equals(lastStatus, "Completed", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                if (string.Equals(lastStatus, "Failed", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"Provisioning failed for tenant {tenantId}: {status?.Error}");
                }
            }

            await Task.Delay(500);
        }

        throw new TimeoutException(
            $"Tenant {tenantId} was not provisioned within {ProvisioningTimeout} (last status: {lastStatus}).");
    }
}
