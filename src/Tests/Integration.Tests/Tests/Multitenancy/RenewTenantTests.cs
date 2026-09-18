#pragma warning disable S1144 // Unused private members — populated by JSON deserialization
#pragma warning disable S3459 // Unassigned members — populated by JSON deserialization
using System.Text.Json;
using Integration.Tests.Infrastructure;

namespace Integration.Tests.Tests.Multitenancy;

/// <summary>
/// Coverage for the tenant renewal endpoint (<c>POST /api/v1/tenants/{id}/renew</c>): extends
/// validity by the configured default term or an explicit month count (stacking on remaining time),
/// route/body mismatch, month-range and empty-tenant validation, and root-only authorization.
/// </summary>
[Collection(AppCollectionDefinition.Name)]
public sealed class RenewTenantTests
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly AppWebApplicationFactory _factory;
    private readonly AuthHelper _auth;

    public RenewTenantTests(AppWebApplicationFactory factory)
    {
        _factory = factory;
        _auth = new AuthHelper(factory);
    }

    #region Happy Path

    [Fact]
    public async Task RenewTenant_Should_Extend_Validity_By_DefaultTerm_When_MonthsOmitted()
    {
        using var rootClient = await _auth.CreateRootAdminClientAsync();
        var unique = Guid.NewGuid().ToString("N")[..8];
        var tenantId = $"renew-{unique}";
        await CreateTenantAsync(rootClient, tenantId, $"renew-{unique}@tenant.com");

        var before = (await GetStatusAsync(rootClient, tenantId)).ValidUpto!.Value;

        var response = await rootClient.PostAsJsonAsync(
            $"{TestConstants.TenantsBasePath}/{tenantId}/renew",
            new { tenantId });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<RenewResult>(Json);
        result.ShouldNotBeNull();
        // Default term is 1 month → validity advances ~1 month from the prior ValidUpto (stacking).
        result.ValidUpto.ShouldBeGreaterThan(before.AddDays(27));
        result.ValidUpto.ShouldBeLessThan(before.AddDays(32));

        var after = (await GetStatusAsync(rootClient, tenantId)).ValidUpto!.Value;
        after.ShouldBe(result.ValidUpto, tolerance: TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task RenewTenant_Should_Extend_Validity_By_ExplicitMonths_When_Supplied()
    {
        using var rootClient = await _auth.CreateRootAdminClientAsync();
        var unique = Guid.NewGuid().ToString("N")[..8];
        var tenantId = $"renew-ex-{unique}";
        await CreateTenantAsync(rootClient, tenantId, $"renew-ex-{unique}@tenant.com");

        var before = (await GetStatusAsync(rootClient, tenantId)).ValidUpto!.Value;

        var response = await rootClient.PostAsJsonAsync(
            $"{TestConstants.TenantsBasePath}/{tenantId}/renew",
            new { tenantId, months = 12 });

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var result = await response.Content.ReadFromJsonAsync<RenewResult>(Json);
        result.ShouldNotBeNull();
        result.ValidUpto.ShouldBe(before.AddMonths(12), tolerance: TimeSpan.FromSeconds(1),
            "an explicit month count wins over the configured default term");

        var after = (await GetStatusAsync(rootClient, tenantId)).ValidUpto!.Value;
        after.ShouldBe(result.ValidUpto, tolerance: TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task RenewTenant_Should_Stack_Two_Terms_When_RenewedTwice()
    {
        using var rootClient = await _auth.CreateRootAdminClientAsync();
        var unique = Guid.NewGuid().ToString("N")[..8];
        var tenantId = $"renew-x2-{unique}";
        await CreateTenantAsync(rootClient, tenantId, $"renew-x2-{unique}@tenant.com");

        var before = (await GetStatusAsync(rootClient, tenantId)).ValidUpto!.Value;

        await RenewAsync(rootClient, tenantId);
        await RenewAsync(rootClient, tenantId);

        var after = (await GetStatusAsync(rootClient, tenantId)).ValidUpto!.Value;
        // Two default (monthly) terms stacked onto the validity present before the renewals.
        after.ShouldBeGreaterThan(before.AddDays(58));
        after.ShouldBeLessThan(before.AddDays(64));
    }

    [Fact]
    public async Task RenewTenant_Should_StartFromNow_When_TenantHasLapsed()
    {
        using var rootClient = await _auth.CreateRootAdminClientAsync();
        var unique = Guid.NewGuid().ToString("N")[..8];
        var tenantId = $"renew-lapsed-{unique}";
        await CreateTenantAsync(rootClient, tenantId, $"renew-lapsed-{unique}@tenant.com");

        // Lapse the tenant 30 days ago (operator override), then renew: stacking must restart from "now",
        // not from the long-past validity, so the tenant gets a full term going forward.
        var adjust = await rootClient.PostAsJsonAsync(
            $"{TestConstants.TenantsBasePath}/{tenantId}/adjust-validity",
            new { tenantId, validUpto = DateTime.UtcNow.AddDays(-30) });
        adjust.StatusCode.ShouldBe(HttpStatusCode.OK, await adjust.Content.ReadAsStringAsync());

        var result = await RenewAsync(rootClient, tenantId);

        result.ValidUpto.ShouldBeGreaterThan(DateTime.UtcNow.AddDays(27),
            "renewing a lapsed tenant must restart the term from now, not stack on the past validity");
        result.ValidUpto.ShouldBeLessThan(DateTime.UtcNow.AddDays(32));
    }

    #endregion

    #region Validation / Bad Request

    [Fact]
    public async Task RenewTenant_Should_Return400_When_RouteIdDoesNotMatchBody()
    {
        using var rootClient = await _auth.CreateRootAdminClientAsync();
        var unique = Guid.NewGuid().ToString("N")[..8];
        var tenantId = $"renew-mm-{unique}";
        await CreateTenantAsync(rootClient, tenantId, $"renew-mm-{unique}@tenant.com");

        var response = await rootClient.PostAsJsonAsync(
            $"{TestConstants.TenantsBasePath}/{tenantId}/renew",
            new { tenantId = "some-other-tenant" });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task RenewTenant_Should_Return400_When_TenantIsEmpty()
    {
        using var rootClient = await _auth.CreateRootAdminClientAsync();
        var unique = Guid.NewGuid().ToString("N")[..8];
        var tenantId = $"renew-empty-{unique}";
        await CreateTenantAsync(rootClient, tenantId, $"renew-empty-{unique}@tenant.com");

        var response = await rootClient.PostAsJsonAsync(
            $"{TestConstants.TenantsBasePath}/{tenantId}/renew",
            new { tenantId = "" });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(121)]
    public async Task RenewTenant_Should_Return400_When_MonthsOutOfRange(int months)
    {
        using var rootClient = await _auth.CreateRootAdminClientAsync();
        var unique = Guid.NewGuid().ToString("N")[..8];
        var tenantId = $"renew-range-{unique}";
        await CreateTenantAsync(rootClient, tenantId, $"renew-range-{unique}@tenant.com");

        var response = await rootClient.PostAsJsonAsync(
            $"{TestConstants.TenantsBasePath}/{tenantId}/renew",
            new { tenantId, months });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    #endregion

    #region AuthZ

    [Fact]
    public async Task RenewTenant_Should_Return401_When_NotAuthenticated()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("tenant", TestConstants.RootTenantId);

        var response = await client.PostAsJsonAsync(
            $"{TestConstants.TenantsBasePath}/anytenant/renew",
            new { tenantId = "anytenant" });

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task RenewTenant_Should_Forbid_When_CallerIsTenantAdmin()
    {
        using var rootClient = await _auth.CreateRootAdminClientAsync();
        var unique = Guid.NewGuid().ToString("N")[..8];
        var tenantId = $"renew-authz-{unique}";
        var adminEmail = $"renew-authz-{unique}@tenant.com";
        await CreateTenantAsync(rootClient, tenantId, adminEmail);
        await WaitForProvisioningAsync(rootClient, tenantId);

        using var tenantClient = await CreateTenantAdminClientWithRetryAsync(
            adminEmail, TestConstants.DefaultPassword, tenantId);

        var response = await tenantClient.PostAsJsonAsync(
            $"{TestConstants.TenantsBasePath}/{tenantId}/renew",
            new { tenantId });

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    #endregion

    #region Helpers

    private async Task<HttpClient> CreateTenantAdminClientWithRetryAsync(
        string email, string password, string tenant, int maxRetries = 30)
    {
        for (var i = 0; i < maxRetries; i++)
        {
            try
            {
                return await _auth.CreateAuthenticatedClientAsync(email, password, tenant);
            }
            catch (HttpRequestException) when (i < maxRetries - 1)
            {
                await Task.Delay(1000);
            }
        }
        return await _auth.CreateAuthenticatedClientAsync(email, password, tenant);
    }

    private static async Task<RenewResult> RenewAsync(HttpClient client, string tenantId)
    {
        var response = await client.PostAsJsonAsync(
            $"{TestConstants.TenantsBasePath}/{tenantId}/renew",
            new { tenantId });
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var result = await response.Content.ReadFromJsonAsync<RenewResult>(Json);
        result.ShouldNotBeNull();
        return result!;
    }

    private static async Task CreateTenantAsync(HttpClient rootClient, string tenantId, string adminEmail)
    {
        var response = await rootClient.PostAsJsonAsync(TestConstants.TenantsBasePath, new
        {
            id = tenantId,
            name = $"Renew {tenantId}",
            connectionString = (string?)null,
            adminEmail,
            adminPassword = TestConstants.DefaultPassword,
            issuer = $"{tenantId}.issuer",
        });
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.ShouldBe(HttpStatusCode.Created, $"Create tenant failed: {body}");
    }

    private static async Task<TenantStatus> GetStatusAsync(HttpClient client, string tenantId)
    {
        var resp = await client.GetAsync($"{TestConstants.TenantsBasePath}/{tenantId}/status");
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var status = await resp.Content.ReadFromJsonAsync<TenantStatus>(Json);
        status.ShouldNotBeNull();
        return status!;
    }

    private static async Task WaitForProvisioningAsync(HttpClient client, string tenantId, int maxRetries = 60)
    {
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

    private sealed record RenewResult(string TenantId, DateTime ValidUpto);

    private sealed record TenantStatus
    {
        public string Id { get; init; } = string.Empty;
        public bool IsActive { get; init; }
        public DateTime? ValidUpto { get; init; }
    }

    #endregion
}
