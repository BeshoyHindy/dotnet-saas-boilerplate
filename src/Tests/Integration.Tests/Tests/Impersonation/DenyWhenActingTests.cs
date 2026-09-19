using System.Net.Http.Json;
using System.Text.Json;
using Integration.Tests.Infrastructure;

namespace Integration.Tests.Tests.Impersonation;

/// <summary>
/// Security-review follow-up: an "acting" token (impersonation or operator token exchange, both
/// carrying <c>act_sub</c>) must never be able to change or reveal the SUBJECT's own credentials.
/// <c>.DenyWhenActing()</c> is the enforcement point — covered here for the two endpoints that
/// matter most (2FA enroll, change password), through both acting mechanisms, with a control case
/// proving the subject's own token still works.
/// </summary>
[Collection(AppCollectionDefinition.Name)]
public sealed class DenyWhenActingTests : IAsyncLifetime
{
    private const string ExchangePath = TestConstants.IdentityBasePath + "/operator/token-exchange";
    private const string ImpersonationStartPath = TestConstants.IdentityBasePath + "/impersonation/start";
    private const string EnrollPath = TestConstants.IdentityBasePath + "/2fa/enroll";
    private const string ChangePasswordPath = TestConstants.IdentityBasePath + "/change-password";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly AppWebApplicationFactory _factory;
    private readonly AuthHelper _auth;

    private string _tenantId = default!;
    private string _tenantAdminEmail = default!;

    public DenyWhenActingTests(AppWebApplicationFactory factory)
    {
        _factory = factory;
        _auth = new AuthHelper(factory);
    }

    public async Task InitializeAsync()
    {
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        _tenantId = $"denyact-{uniqueId}";
        _tenantAdminEmail = $"admin-{uniqueId}@denyact.com";

        using var rootClient = await _auth.CreateRootAdminClientAsync();
        await TenantFixture.CreateTenantAsync(rootClient, _tenantId, _tenantAdminEmail);
        await TenantFixture.WaitForProvisioningAsync(rootClient, _tenantId);

        // Provisioning completing doesn't guarantee the tenant admin can log in on the very next
        // request (eventual consistency) — same reason OperatorTokenExchangeTests warms this up.
        await TenantFixture.GetTokenWithRetryAsync(_auth, _tenantAdminEmail, TestConstants.DefaultPassword, _tenantId);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ─── Exchanged token (cross-tenant) ─────────────────────────────────

    #region Operator token exchange

    [Fact]
    public async Task Enroll_Should_Return403_When_TokenIsExchanged()
    {
        // Arrange
        using var rootClient = await _auth.CreateRootAdminClientAsync();
        using var actingClient = ClientWithBearer(await ExchangeAsync(rootClient, _tenantId));

        // Act
        var response = await actingClient.PostAsJsonAsync(EnrollPath, new { });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ChangePassword_Should_Return403_And_LeavePasswordUnchanged_When_TokenIsExchanged()
    {
        // Arrange
        using var rootClient = await _auth.CreateRootAdminClientAsync();
        using var actingClient = ClientWithBearer(await ExchangeAsync(rootClient, _tenantId));

        // Act — try to hijack the subject's (the tenant admin's) own password.
        var response = await actingClient.PostAsJsonAsync(ChangePasswordPath, new
        {
            password = TestConstants.DefaultPassword,
            newPassword = "Hijacked123!",
            confirmNewPassword = "Hijacked123!",
        });

        // Assert — the filter short-circuits before the handler runs, so the original password
        // must still authenticate.
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var token = await _auth.GetTokenAsync(_tenantAdminEmail, TestConstants.DefaultPassword, _tenantId);
        token.AccessToken.ShouldNotBeNullOrWhiteSpace();
    }

    #endregion

    // ─── Impersonation token (same-tenant) ──────────────────────────────

    #region Impersonation

    [Fact]
    public async Task Enroll_Should_Return403_When_TokenIsImpersonation()
    {
        // Arrange
        using var tenantAdminClient = await CreateTenantAdminClientAsync();
        var member = await TenantFixture.RegisterAndConfirmUserAsync(_factory, tenantAdminClient, _tenantId, "denyactmember1");
        using var actingClient = ClientWithBearer(await StartImpersonationAsync(tenantAdminClient, member.UserId, _tenantId));

        // Act
        var response = await actingClient.PostAsJsonAsync(EnrollPath, new { });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ChangePassword_Should_Return403_And_LeavePasswordUnchanged_When_TokenIsImpersonation()
    {
        // Arrange
        using var tenantAdminClient = await CreateTenantAdminClientAsync();
        var member = await TenantFixture.RegisterAndConfirmUserAsync(_factory, tenantAdminClient, _tenantId, "denyactmember2");
        using var actingClient = ClientWithBearer(await StartImpersonationAsync(tenantAdminClient, member.UserId, _tenantId));

        // Act — the admin, acting as the member, tries to hijack the member's own password.
        var response = await actingClient.PostAsJsonAsync(ChangePasswordPath, new
        {
            password = member.Password,
            newPassword = "Hijacked123!",
            confirmNewPassword = "Hijacked123!",
        });

        // Assert — the member's original password still authenticates.
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var token = await _auth.GetTokenAsync(member.Email, member.Password, _tenantId);
        token.AccessToken.ShouldNotBeNullOrWhiteSpace();
    }

    #endregion

    // ─── Control: the subject's own token still works ───────────────────

    #region Own token

    [Fact]
    public async Task ChangePassword_Should_Succeed_When_CallerUsesTheirOwnToken()
    {
        // Arrange — same tenant, same endpoint, but no act_sub: the caller is the subject.
        using var tenantAdminClient = await CreateTenantAdminClientAsync();
        var member = await TenantFixture.RegisterAndConfirmUserAsync(_factory, tenantAdminClient, _tenantId, "denyactmember3");
        using var ownClient = await _auth.CreateAuthenticatedClientAsync(member.Email, member.Password, _tenantId);
        const string newPassword = "OwnToken123!";

        // Act
        var response = await ownClient.PostAsJsonAsync(ChangePasswordPath, new
        {
            password = member.Password,
            newPassword,
            confirmNewPassword = newPassword,
        });

        // Assert — DenyWhenActing must not fire for a token with no act_sub claim.
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var token = await _auth.GetTokenAsync(member.Email, newPassword, _tenantId);
        token.AccessToken.ShouldNotBeNullOrWhiteSpace();
    }

    #endregion

    // ─── helpers ────────────────────────────────────────────────────────

    private static async Task<string> ExchangeAsync(HttpClient rootClient, string targetTenantId)
    {
        var response = await rootClient.PostAsJsonAsync(ExchangePath, new
        {
            targetTenantId,
            reason = "deny-when-acting test fixture",
        });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<ExchangeResponse>(Json);
        return body!.AccessToken;
    }

    private static async Task<string> StartImpersonationAsync(HttpClient asClient, string targetUserId, string targetTenantId)
    {
        var response = await asClient.PostAsJsonAsync(ImpersonationStartPath, new
        {
            targetUserId,
            targetTenantId,
            reason = "deny-when-acting test fixture",
        });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<ImpersonationResponse>(Json);
        return body!.AccessToken;
    }

    private HttpClient ClientWithBearer(string accessToken)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        return client;
    }

    private Task<HttpClient> CreateTenantAdminClientAsync() =>
        _auth.CreateAuthenticatedClientAsync(_tenantAdminEmail, TestConstants.DefaultPassword, _tenantId);

    private sealed class ExchangeResponse
    {
        public string AccessToken { get; set; } = default!;
    }

    private sealed class ImpersonationResponse
    {
        public string AccessToken { get; set; } = default!;
    }
}
