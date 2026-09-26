using Boilerplate.Modules.Identity.Contracts;
using Integration.Tests.Infrastructure;
using Integration.Tests.Infrastructure.Extensions;

namespace Integration.Tests.Tests.Multitenancy;

[Collection(AppCollectionDefinition.Name)]
public sealed class TenantCreationTests
{
    private readonly AppWebApplicationFactory _factory;
    private readonly AuthHelper _auth;

    public TenantCreationTests(AppWebApplicationFactory factory)
    {
        _factory = factory;
        _auth = new AuthHelper(factory);
    }

    [Fact]
    public async Task CreateTenant_Should_Return201WithId_When_DataIsValid()
    {
        using var client = await _auth.CreateRootAdminClientAsync();
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var tenantId = $"t-{uniqueId}";

        var response = await client.PostAsJsonAsync(TestConstants.TenantsBasePath, new
        {
            id = tenantId,
            name = $"Test Tenant {uniqueId}",
            adminEmail = $"admin-{uniqueId}@tenant.com",
            adminPassword = TestConstants.DefaultPassword,
            issuer = "test.issuer"
        });

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var result = await response.DeserializeAsync<CreateTenantResult>();
        result.Id.ShouldBe(tenantId);
    }

    [Fact]
    public async Task CreateTenant_Should_Apply_DefaultValidityTerm_When_ValidUptoOmitted()
    {
        using var client = await _auth.CreateRootAdminClientAsync();
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var tenantId = $"tv-def-{uniqueId}";

        var response = await client.PostAsJsonAsync(TestConstants.TenantsBasePath, new
        {
            id = tenantId,
            name = $"Default Validity {uniqueId}",
            adminEmail = $"tvdef-{uniqueId}@tenant.com",
            adminPassword = TestConstants.DefaultPassword,
            issuer = "tvdef.issuer"
        });
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());

        // TenantValidity:DefaultValidityMonths is 1 → validity lands roughly a month out.
        var validUpto = await GetValidUptoAsync(client, tenantId);
        validUpto.ShouldBeGreaterThan(DateTime.UtcNow.AddDays(27));
        validUpto.ShouldBeLessThan(DateTime.UtcNow.AddDays(32));
    }

    [Fact]
    public async Task CreateTenant_Should_Use_ExplicitValidUpto_When_Supplied()
    {
        using var client = await _auth.CreateRootAdminClientAsync();
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var tenantId = $"tv-exp-{uniqueId}";
        var target = DateTime.UtcNow.AddYears(3);

        var response = await client.PostAsJsonAsync(TestConstants.TenantsBasePath, new
        {
            id = tenantId,
            name = $"Explicit Validity {uniqueId}",
            adminEmail = $"tvexp-{uniqueId}@tenant.com",
            adminPassword = TestConstants.DefaultPassword,
            issuer = "tvexp.issuer",
            validUpto = target
        });
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());

        var validUpto = await GetValidUptoAsync(client, tenantId);
        validUpto.ShouldBe(target, tolerance: TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task CreateTenant_Should_Reject_When_ValidUptoIsInThePast()
    {
        using var client = await _auth.CreateRootAdminClientAsync();
        var uniqueId = Guid.NewGuid().ToString("N")[..8];

        var response = await client.PostAsJsonAsync(TestConstants.TenantsBasePath, new
        {
            id = $"tv-past-{uniqueId}",
            name = $"Past Validity {uniqueId}",
            adminEmail = $"tvpast-{uniqueId}@tenant.com",
            adminPassword = TestConstants.DefaultPassword,
            issuer = "tvpast.issuer",
            validUpto = DateTime.UtcNow.AddDays(-1)
        });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CreateTenant_Should_Return400NamingTheRealMinimum_When_AdminPasswordIsNineCharacters()
    {
        using var client = await _auth.CreateRootAdminClientAsync();
        var uniqueId = Guid.NewGuid().ToString("N")[..8];

        var response = await client.PostAsJsonAsync(TestConstants.TenantsBasePath, new
        {
            id = $"tpw-short-{uniqueId}",
            name = $"Short Admin Password {uniqueId}",
            adminEmail = $"tpwshort-{uniqueId}@tenant.com",
            adminPassword = "Abcdef12!",
            issuer = "tpwshort.issuer"
        });

        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, body);
        body.ShouldContain($"at least {PasswordPolicy.MinimumLength} characters");
    }

    [Fact]
    public async Task CreateTenant_Should_Return400_When_AdminPasswordIsCommon()
    {
        // Refused on the request: the admin is created later by the provisioning seed step, where a
        // policy failure could only surface as a Failed provisioning.
        using var client = await _auth.CreateRootAdminClientAsync();
        var uniqueId = Guid.NewGuid().ToString("N")[..8];

        var response = await client.PostAsJsonAsync(TestConstants.TenantsBasePath, new
        {
            id = $"tpw-common-{uniqueId}",
            name = $"Common Admin Password {uniqueId}",
            adminEmail = $"tpwcommon-{uniqueId}@tenant.com",
            adminPassword = "password123",
            issuer = "tpwcommon.issuer"
        });

        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, body);
        body.ShouldContain(PasswordPolicy.CommonPasswordMessage);
    }

    [Fact]
    public async Task CreateTenant_Should_Reject_When_IdAlreadyExists()
    {
        using var client = await _auth.CreateRootAdminClientAsync();
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var payload = new
        {
            id = $"dup-{uniqueId}",
            name = $"Dup Tenant {uniqueId}",
            adminEmail = $"dupadmin-{uniqueId}@tenant.com",
            adminPassword = TestConstants.DefaultPassword,
            issuer = "dup.issuer"
        };

        var firstResponse = await client.PostAsJsonAsync(TestConstants.TenantsBasePath, payload);
        firstResponse.StatusCode.ShouldBe(HttpStatusCode.Created);

        var secondResponse = await client.PostAsJsonAsync(TestConstants.TenantsBasePath, payload);

        secondResponse.IsSuccessStatusCode.ShouldBeFalse();
    }

    [Fact]
    public async Task CreateTenant_Should_Return401_When_NotAuthenticated()
    {
        using var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(TestConstants.TenantsBasePath, new
        {
            id = "noauth",
            name = "No Auth Tenant",
            adminEmail = "noauth@tenant.com",
            issuer = "noauth.issuer"
        });

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetTenants_Should_ReturnOk_When_AuthenticatedAsRootAdmin()
    {
        using var client = await _auth.CreateRootAdminClientAsync();

        var response = await client.GetAsync($"{TestConstants.TenantsBasePath}?pageNumber=1&pageSize=50");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetTenantStatus_Should_ReturnOk_When_TenantExists()
    {
        using var client = await _auth.CreateRootAdminClientAsync();

        var response = await client.GetAsync($"{TestConstants.TenantsBasePath}/{TestConstants.RootTenantId}/status");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private static async Task<DateTime> GetValidUptoAsync(HttpClient client, string tenantId)
    {
        var response = await client.GetAsync($"{TestConstants.TenantsBasePath}/{tenantId}/status");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var status = await response.DeserializeAsync<TenantValidityStatus>();
        return status.ValidUpto;
    }

    private sealed record TenantValidityStatus(string Id, DateTime ValidUpto);
}
