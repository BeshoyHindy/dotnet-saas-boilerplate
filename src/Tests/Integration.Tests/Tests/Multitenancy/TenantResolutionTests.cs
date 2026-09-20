#pragma warning disable S1144 // Unused private members — populated by JSON
#pragma warning disable S3459 // Unassigned members — populated by JSON
using Boilerplate.BuildingBlocks.Shared.Constants;
using Integration.Tests.Infrastructure;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Text.Json;

namespace Integration.Tests.Tests.Multitenancy;

/// <summary>
/// ADR-0002 acceptance coverage, on real PostgreSQL. The tenant is resolved from the signed
/// token's <c>tenant</c> claim and — for anonymous auth routes only — the <c>{tenant}</c> route
/// value. Nothing the caller puts on the wire can name a tenant, so:
///
/// <list type="bullet">
/// <item>a forged <c>tenant</c> header or <c>?tenant=</c> on an authenticated call changes nothing,
/// for a tenant operator and for a root operator alike (the root header override is gone);</item>
/// <item>logging in on tenant A's route with tenant B's credentials fails;</item>
/// <item>a validly signed token with no <c>tenant</c> claim is rejected 401.</item>
/// </list>
///
/// This class replaces TenantHeaderOverrideTests, which pinned the deleted override.
/// </summary>
[Collection(AppCollectionDefinition.Name)]
public sealed class TenantResolutionTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private const string SearchPath =
        $"{TestConstants.IdentityBasePath}/users/search?PageNumber=1&PageSize=50";

    private readonly AppWebApplicationFactory _factory;
    private readonly AuthHelper _auth;

    private string _tenantA = default!;
    private string _tenantAAdminEmail = default!;
    private string _tenantB = default!;
    private string _tenantBAdminEmail = default!;

    public TenantResolutionTests(AppWebApplicationFactory factory)
    {
        _factory = factory;
        _auth = new AuthHelper(factory);
    }

    public async Task InitializeAsync()
    {
        var unique = Guid.NewGuid().ToString("N")[..8];
        _tenantA = $"tres-a-{unique}";
        _tenantB = $"tres-b-{unique}";
        _tenantAAdminEmail = $"admin-a-{unique}@tres.com";
        _tenantBAdminEmail = $"admin-b-{unique}@tres.com";

        using var rootClient = await _auth.CreateRootAdminClientAsync();
        await CreateTenantAsync(rootClient, _tenantA, _tenantAAdminEmail);
        await CreateTenantAsync(rootClient, _tenantB, _tenantBAdminEmail);
        await WaitForProvisioningAsync(rootClient, _tenantA);
        await WaitForProvisioningAsync(rootClient, _tenantB);

        // Provisioning can report "Completed" a tick before the seeded admin user is queryable; a
        // successful token issuance cross-checks the user exists, avoiding a first-test race.
        _ = await GetTokenWithRetryAsync(_tenantAAdminEmail, TestConstants.DefaultPassword, _tenantA);
        _ = await GetTokenWithRetryAsync(_tenantBAdminEmail, TestConstants.DefaultPassword, _tenantB);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ─── forged tenant inputs on an authenticated call ───────────────────

    [Fact]
    public async Task ForgedTenantHeader_Should_HaveNoEffect_On_AuthenticatedCall()
    {
        // Arrange — tenant A's admin, with a `tenant: B` header bolted on.
        var tokenA = await GetTokenWithRetryAsync(_tenantAAdminEmail, TestConstants.DefaultPassword, _tenantA);
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", tokenA.AccessToken);
        client.DefaultRequestHeaders.Add("tenant", _tenantB);

        // Act
        var response = await client.GetAsync(SearchPath);

        // Assert — the header is never read, so the query stays in tenant A.
        var page = await ReadPageAsync(response);
        page.Items.ShouldContain(u => u.Email == _tenantAAdminEmail);
        page.Items.ShouldNotContain(u => u.Email == _tenantBAdminEmail);
    }

    [Fact]
    public async Task ForgedTenantQueryString_Should_HaveNoEffect_On_AuthenticatedCall()
    {
        // Arrange
        var tokenA = await GetTokenWithRetryAsync(_tenantAAdminEmail, TestConstants.DefaultPassword, _tenantA);
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", tokenA.AccessToken);

        // Act — the retired ?tenant= delegate strategy would have switched tenants here.
        var response = await client.GetAsync($"{SearchPath}&tenant={_tenantB}");

        // Assert
        var page = await ReadPageAsync(response);
        page.Items.ShouldContain(u => u.Email == _tenantAAdminEmail);
        page.Items.ShouldNotContain(u => u.Email == _tenantBAdminEmail);
    }

    [Fact]
    public async Task RootOperator_Should_StayInRoot_When_ForeignTenantHeaderIsSent()
    {
        // Arrange — the case the deleted root header override used to serve: a root token plus a
        // `tenant: A` header. Root operators now cross tenants only by exchanging tokens.
        var rootToken = await _auth.GetRootAdminTokenAsync();
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", rootToken.AccessToken);
        client.DefaultRequestHeaders.Add("tenant", _tenantA);

        // Act
        var response = await client.GetAsync(SearchPath);

        // Assert — the request stays in root: root's admin is visible, tenant A's is not.
        var page = await ReadPageAsync(response);
        page.Items.ShouldContain(u => u.Email == TestConstants.RootAdminEmail);
        page.Items.ShouldNotContain(u => u.Email == _tenantAAdminEmail);
    }

    [Fact]
    public async Task TenantAdmin_Should_AccessOwnTenant_Normally()
    {
        // Sanity check that the single strategy doesn't break the ordinary path.
        using var client = await _auth.CreateAuthenticatedClientAsync(
            _tenantAAdminEmail, TestConstants.DefaultPassword, _tenantA);

        var response = await client.GetAsync(SearchPath);

        var page = await ReadPageAsync(response);
        page.Items.ShouldContain(u => u.Email == _tenantAAdminEmail);
    }

    // ─── the anonymous auth route ────────────────────────────────────────

    [Fact]
    public async Task Login_Should_Fail_When_TenantBCredentialsAreUsedOnTenantARoute()
    {
        // Arrange — tenant B's admin credentials, posted to tenant A's login route.
        using var client = _factory.CreateClient();

        // Act
        var response = await client.PostAsJsonAsync(
            $"{TestConstants.AuthBasePath(_tenantA)}/token",
            new { email = _tenantBAdminEmail, password = TestConstants.DefaultPassword });

        // Assert — the user row lives in tenant B, so inside tenant A there is no such user.
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Login_Should_Issue_TenantScopedToken_When_CredentialsMatchTheRouteTenant()
    {
        var token = await GetTokenWithRetryAsync(_tenantAAdminEmail, TestConstants.DefaultPassword, _tenantA);

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token.AccessToken);
        jwt.Claims.First(c => c.Type == ClaimConstants.Tenant).Value.ShouldBe(_tenantA);
    }

    [Fact]
    public async Task Login_Should_Ignore_ForgedTenantHeader_On_TheRoute()
    {
        // Arrange — tenant A's route, but a `tenant: B` header attached.
        using var client = _factory.CreateClient();
        var request = new HttpRequestMessage(
            HttpMethod.Post, $"{TestConstants.AuthBasePath(_tenantA)}/token");
        request.Headers.Add("tenant", _tenantB);
        request.Content = JsonContent.Create(
            new { email = _tenantAAdminEmail, password = TestConstants.DefaultPassword });

        // Act
        var response = await client.SendAsync(request);

        // Assert — the route wins; the issued token names tenant A.
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var token = JsonSerializer.Deserialize<TokenResult>(
            await response.Content.ReadAsStringAsync(), Json)!;
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token.AccessToken);
        jwt.Claims.First(c => c.Type == ClaimConstants.Tenant).Value.ShouldBe(_tenantA);
    }

    // ─── token without a usable tenant claim ─────────────────────────────

    [Fact]
    public async Task AuthenticatedCall_Should_Return401_When_TokenHasNoTenantClaim()
    {
        // Arrange — a token signed with the host's real key but carrying no `tenant` claim.
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", MintToken(tenantClaim: null));

        // Act
        var response = await client.GetAsync(SearchPath);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task AuthenticatedCall_Should_Return401_When_TenantClaimIsBlank()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", MintToken(tenantClaim: "   "));

        var response = await client.GetAsync(SearchPath);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task AuthenticatedCall_Should_Return401_When_TenantClaimNamesAnUnknownTenant()
    {
        // A claim that no longer resolves (tenant deleted, or simply never existed) is as unusable
        // as a missing one: we refuse rather than run with an ambient tenant of "none".
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new("Bearer", MintToken($"ghost-{Guid.NewGuid():N}"));

        var response = await client.GetAsync(SearchPath);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    // ─── deactivated / expired guard still applies ───────────────────────

    [Fact]
    public async Task DeactivatedTenant_Should_Return403_For_AuthenticatedAndAnonymousRoutes()
    {
        // Arrange — tenant B's admin holds a valid token, then tenant B is deactivated.
        var tokenB = await GetTokenWithRetryAsync(_tenantBAdminEmail, TestConstants.DefaultPassword, _tenantB);
        using (var rootClient = await _auth.CreateRootAdminClientAsync())
        {
            var deactivate = await rootClient.PostAsJsonAsync(
                $"{TestConstants.TenantsBasePath}/{_tenantB}/activation",
                new { tenantId = _tenantB, isActive = false });
            deactivate.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        // Act — the authenticated path (tenant from the claim) …
        using var authenticated = _factory.CreateClient();
        authenticated.DefaultRequestHeaders.Authorization = new("Bearer", tokenB.AccessToken);
        var authenticatedResponse = await authenticated.GetAsync(SearchPath);

        // … and the anonymous path (tenant from the route).
        using var anonymous = _factory.CreateClient();
        var anonymousResponse = await anonymous.PostAsJsonAsync(
            $"{TestConstants.AuthBasePath(_tenantB)}/token",
            new { email = _tenantBAdminEmail, password = TestConstants.DefaultPassword });

        // Assert — the guard runs off the resolved tenant on both paths.
        authenticatedResponse.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        anonymousResponse.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    // ─── helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// Mints a JWT with the factory's signing key, issuer and audience, so JwtBearer accepts it —
    /// the only thing under test is what the pipeline does with (or without) the tenant claim.
    /// </summary>
    private static string MintToken(string? tenantClaim)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, Guid.NewGuid().ToString()),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new(JwtRegisteredClaimNames.Email, "nobody@example.com"),
        };

        if (tenantClaim is not null)
        {
            claims.Add(new Claim(ClaimConstants.Tenant, tenantClaim));
        }

        var key = new SymmetricSecurityKey(Encoding.ASCII.GetBytes(TestConstants.JwtSigningKey));
        var token = new JwtSecurityToken(
            issuer: TestConstants.JwtIssuer,
            audience: TestConstants.JwtAudience,
            claims: claims,
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: DateTime.UtcNow.AddMinutes(30),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static async Task<PagedResult<SearchUserDto>> ReadPageAsync(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var page = await response.Content.ReadFromJsonAsync<PagedResult<SearchUserDto>>(Json);
        page.ShouldNotBeNull();
        return page;
    }

    private async Task<TokenResult> GetTokenWithRetryAsync(
        string email, string password, string tenant, int maxRetries = 30)
    {
        Exception? last = null;
        for (var i = 0; i < maxRetries; i++)
        {
            try
            {
                return await _auth.GetTokenAsync(email, password, tenant);
            }
            catch (HttpRequestException ex)
            {
                last = ex;
                await Task.Delay(500);
            }
        }
        throw last ?? new InvalidOperationException("token issuance failed");
    }

    private static async Task CreateTenantAsync(HttpClient rootClient, string tenantId, string adminEmail)
    {
        var response = await rootClient.PostAsJsonAsync(TestConstants.TenantsBasePath, new
        {
            id = tenantId,
            name = $"TRES {tenantId}",
            adminEmail,
            adminPassword = TestConstants.DefaultPassword,
            issuer = $"{tenantId}.issuer",
        });
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
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

    private sealed class SearchUserDto
    {
        public string? Id { get; set; }
        public string? UserName { get; set; }
        public string? Email { get; set; }
    }
}
