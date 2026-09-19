// Test-only DTOs are populated by System.Text.Json via reflection — the
// SonarAnalyzer can't see the assignments and warns about "unused" private
// setters. Suppressing the noise file-wide rather than annotating each DTO.
#pragma warning disable S1144 // Unused private types or members should be removed
#pragma warning disable S3459 // Unassigned members should be removed
using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Integration.Tests.Infrastructure;

namespace Integration.Tests.Tests.Impersonation;

/// <summary>
/// End-to-end coverage of impersonation: start, end, the JWT revocation hook, per-grant
/// persistence and the duration cap.
///
/// Since #9 impersonation is SAME-TENANT only — an admin acting as one of their own tenant's
/// users. Crossing a tenant boundary is the operator token exchange
/// (<c>POST /identity/operator/token-exchange</c>, covered by OperatorTokenExchangeTests), which
/// mints through the same issuer into the same grant table. Tests here that need a cross-tenant
/// grant therefore create it through the exchange.
/// </summary>
[Collection(AppCollectionDefinition.Name)]
public sealed class ImpersonationTests : IAsyncLifetime
{
    private const string ImpersonationBasePath = TestConstants.IdentityBasePath + "/impersonation";
    private const string ExchangePath = TestConstants.IdentityBasePath + "/operator/token-exchange";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly AppWebApplicationFactory _factory;
    private readonly AuthHelper _auth;

    // Populated by InitializeAsync — a freshly provisioned tenant with a known admin and a plain
    // member (the same-tenant impersonation target).
    private string _tenantId = default!;
    private string _tenantAdminEmail = default!;
    private string _tenantAdminUserId = default!;
    private string _tenantMemberUserId = default!;
    private string _rootAdminUserId = default!;

    public ImpersonationTests(AppWebApplicationFactory factory)
    {
        _factory = factory;
        _auth = new AuthHelper(factory);
    }

    public async Task InitializeAsync()
    {
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        _tenantId = $"imptest-{uniqueId}";
        _tenantAdminEmail = $"admin-{uniqueId}@imptest.com";

        using var rootClient = await _auth.CreateRootAdminClientAsync();
        await TenantFixture.CreateTenantAsync(rootClient, _tenantId, _tenantAdminEmail);
        await TenantFixture.WaitForProvisioningAsync(rootClient, _tenantId);

        // Sign in as the seeded tenant admin to capture their userId from the JWT — the search
        // endpoint would couple these tests to the user-search surface.
        var tenantToken = await TenantFixture.GetTokenWithRetryAsync(
            _auth, _tenantAdminEmail, TestConstants.DefaultPassword, _tenantId);
        _tenantAdminUserId = ReadSubject(tenantToken.AccessToken);

        using var tenantAdminClient = await CreateTenantAdminClientAsync();
        var member = await TenantFixture.RegisterAndConfirmUserAsync(
            _factory, tenantAdminClient, _tenantId, "member");
        _tenantMemberUserId = member.UserId;

        var rootToken = await _auth.GetRootAdminTokenAsync();
        _rootAdminUserId = ReadSubject(rootToken.AccessToken);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ─── StartImpersonation ─────────────────────────────────────────────

    #region Happy Path

    [Fact]
    public async Task Start_Should_IssueImpersonationToken_When_AdminImpersonatesOwnTenantUser()
    {
        // Arrange
        using var tenantClient = await CreateTenantAdminClientAsync();

        // Act
        var response = await tenantClient.PostAsJsonAsync($"{ImpersonationBasePath}/start", new
        {
            targetUserId = _tenantMemberUserId,
            targetTenantId = _tenantId,
            reason = "reproducing a support ticket",
            durationMinutes = 15,
        });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ImpersonationResponse>(Json);
        body.ShouldNotBeNull();
        body.AccessToken.ShouldNotBeNullOrWhiteSpace();
        body.ActorUserId.ShouldBe(_tenantAdminUserId);
        body.ActorTenantId.ShouldBe(_tenantId);
        body.ImpersonatedUserId.ShouldBe(_tenantMemberUserId);
        body.ImpersonatedTenantId.ShouldBe(_tenantId);
    }

    [Fact]
    public async Task Start_Should_EmbedActorClaims_In_IssuedToken()
    {
        // Arrange
        using var tenantClient = await CreateTenantAdminClientAsync();

        // Act
        var token = await StartImpersonationAsync(tenantClient, _tenantMemberUserId, _tenantId);

        // Assert — the impersonation token must carry act_sub/act_tenant so the audit trail and
        // the End handler know who is really acting.
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        jwt.Claims.ShouldContain(c => c.Type == "act_sub" && c.Value == _tenantAdminUserId);
        jwt.Claims.ShouldContain(c => c.Type == "act_tenant" && c.Value == _tenantId);
        jwt.Subject.ShouldBe(_tenantMemberUserId);
    }

    [Fact]
    public async Task Start_Should_HonorRequestedDuration_When_WithinCap()
    {
        // Arrange
        using var tenantClient = await CreateTenantAdminClientAsync();
        var before = DateTime.UtcNow;

        // Act
        var response = await tenantClient.PostAsJsonAsync($"{ImpersonationBasePath}/start", new
        {
            targetUserId = _tenantMemberUserId,
            targetTenantId = _tenantId,
            reason = "duration override check",
            durationMinutes = 10,
        });

        // Assert — expiry should be within a few seconds of `now + 10 min`. The
        // default AccessTokenMinutes is 30 in the test config, so 10 must NOT be
        // coming from the default.
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ImpersonationResponse>(Json);
        var expectedMin = before.AddMinutes(10).AddSeconds(-5);
        var expectedMax = before.AddMinutes(10).AddSeconds(60);
        body!.AccessTokenExpiresAt.ShouldBeInRange(expectedMin, expectedMax);
    }

    #endregion

    #region Validation + Authorization

    [Fact]
    public async Task Start_Should_RejectInvalidDuration_When_ExceedsCap()
    {
        // Arrange
        using var tenantClient = await CreateTenantAdminClientAsync();

        // Act — the ceiling is shared with the operator exchange (OperatorExchange:MaxMinutes);
        // 999 is absurd and bounces up front.
        var response = await tenantClient.PostAsJsonAsync($"{ImpersonationBasePath}/start", new
        {
            targetUserId = _tenantMemberUserId,
            targetTenantId = _tenantId,
            reason = "trying to escape the cap",
            durationMinutes = 999,
        });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Start_Should_RejectCrossTenant_When_CallerIsRootOperator()
    {
        // Arrange — since #9 there is exactly one cross-tenant door, and this is not it.
        using var rootClient = await _auth.CreateRootAdminClientAsync();

        // Act
        var response = await rootClient.PostAsJsonAsync($"{ImpersonationBasePath}/start", new
        {
            targetUserId = _tenantAdminUserId,
            targetTenantId = _tenantId,
            reason = "root reaching across without an exchange",
        });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Start_Should_RejectCrossTenant_When_CallerIsNotRoot()
    {
        // Arrange — tenant admin tries to impersonate into root tenant.
        using var tenantClient = await CreateTenantAdminClientAsync();

        // Act
        var response = await tenantClient.PostAsJsonAsync($"{ImpersonationBasePath}/start", new
        {
            targetUserId = _rootAdminUserId,
            targetTenantId = TestConstants.RootTenantId,
            reason = "trying to escalate to root",
        });

        // Assert — server-side check throws ForbiddenException.
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Start_Should_RejectSelfImpersonation()
    {
        // Arrange — root admin tries to impersonate themselves.
        using var rootClient = await _auth.CreateRootAdminClientAsync();

        // Act
        var response = await rootClient.PostAsJsonAsync($"{ImpersonationBasePath}/start", new
        {
            targetUserId = _rootAdminUserId,
            targetTenantId = TestConstants.RootTenantId,
            reason = "pointless self-loop",
        });

        // Assert — handler throws CustomException for this; framework maps to 4xx.
        response.IsSuccessStatusCode.ShouldBeFalse();
        ((int)response.StatusCode).ShouldBeInRange(400, 499);
    }

    [Fact]
    public async Task Start_Should_RejectNestedImpersonation()
    {
        // Arrange — start one impersonation, then try to start another using the
        // impersonation token.
        using var tenantClient = await CreateTenantAdminClientAsync();
        var impersonationToken = await StartImpersonationAsync(tenantClient, _tenantMemberUserId, _tenantId);

        using var nestedClient = ClientWithBearer(impersonationToken);

        // Act
        var response = await nestedClient.PostAsJsonAsync($"{ImpersonationBasePath}/start", new
        {
            targetUserId = _tenantAdminUserId,
            targetTenantId = _tenantId,
            reason = "nested attempt",
        });

        // Assert
        response.IsSuccessStatusCode.ShouldBeFalse();
        ((int)response.StatusCode).ShouldBeInRange(400, 499);
    }

    [Fact]
    public async Task Start_Should_Return404_When_TargetUserDoesNotExistInTenant()
    {
        // Arrange — a user id that is not in the caller's tenant must 404, not leak.
        using var tenantClient = await CreateTenantAdminClientAsync();

        // Act
        var response = await tenantClient.PostAsJsonAsync($"{ImpersonationBasePath}/start", new
        {
            targetUserId = Guid.NewGuid().ToString(),
            targetTenantId = _tenantId,
            reason = "unknown user id",
        });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Start_Should_Return401_When_Anonymous()
    {
        // Arrange
        using var anonClient = _factory.CreateClient();

        // Act
        var response = await anonClient.PostAsJsonAsync($"{ImpersonationBasePath}/start", new
        {
            targetUserId = _tenantMemberUserId,
            targetTenantId = _tenantId,
            reason = "anonymous",
        });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    #endregion

    // ─── EndImpersonation ───────────────────────────────────────────────

    #region End

    [Fact]
    public async Task End_Should_ReturnNoToken_When_SessionIsImpersonation()
    {
        // Arrange
        using var tenantClient = await CreateTenantAdminClientAsync();
        var impersonationToken = await StartImpersonationAsync(tenantClient, _tenantMemberUserId, _tenantId);

        using var endClient = ClientWithBearer(impersonationToken);

        // Act
        var response = await endClient.PostAsync($"{ImpersonationBasePath}/end", content: null);

        // Assert — End hands back no credential at all: the actor's own session was never taken
        // away, so there is nothing to restore (and nothing minted without a refresh counterpart).
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var raw = await response.Content.ReadAsStringAsync();
        raw.ShouldNotContain("accessToken", Case.Insensitive);
        raw.ShouldNotContain("refreshToken", Case.Insensitive);

        var body = await response.Content.ReadFromJsonAsync<EndImpersonationPayload>(Json);
        body.ShouldNotBeNull();
        body.ActorUserId.ShouldBe(_tenantAdminUserId);
        body.ActorTenantId.ShouldBe(_tenantId);
        body.ImpersonatedUserId.ShouldBe(_tenantMemberUserId);

        // …and the impersonation token is dead from here on.
        var after = await endClient.GetAsync($"{TestConstants.IdentityBasePath}/profile");
        after.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task End_Should_Reject_When_SessionIsNotImpersonation()
    {
        // Arrange — a normal root admin client has no act_sub claim.
        using var rootClient = await _auth.CreateRootAdminClientAsync();

        // Act
        var response = await rootClient.PostAsync($"{ImpersonationBasePath}/end", content: null);

        // Assert
        response.IsSuccessStatusCode.ShouldBeFalse();
        ((int)response.StatusCode).ShouldBeInRange(400, 499);
    }

    #endregion

    // ─── Grant lifecycle + revocation ──────────────────────────────────

    #region Grants

    [Fact]
    public async Task GetGrants_Should_ListActiveGrant_After_Exchange()
    {
        // Arrange — a cross-tenant grant, created the only way there is: an exchange.
        using var rootClient = await _auth.CreateRootAdminClientAsync();
        _ = await ExchangeAsync(rootClient, _tenantId);

        // Act
        var response = await rootClient.GetAsync($"{ImpersonationBasePath}/grants?Status=Active");

        // Assert — the just-created grant must be visible to the root operator.
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var grants = await response.Content.ReadFromJsonAsync<List<ImpersonationGrantPayload>>(Json);
        grants.ShouldNotBeNull();
        grants.ShouldContain(g =>
            g.ImpersonatedUserId == _tenantAdminUserId
            && g.ImpersonatedTenantId == _tenantId
            && g.ActorUserId == _rootAdminUserId
            && g.Status == "Active");
    }

    [Fact]
    public async Task GetGrants_Should_ScopeByTenant_When_CallerIsTenantAdmin()
    {
        // Arrange — an operator exchange targeting the test tenant.
        using var rootClient = await _auth.CreateRootAdminClientAsync();
        _ = await ExchangeAsync(rootClient, _tenantId);

        // The tenant admin lists grants from their own tenant context.
        using var tenantClient = await CreateTenantAdminClientAsync();

        // Act
        var response = await tenantClient.GetAsync($"{ImpersonationBasePath}/grants");

        // Assert — tenant admin should see the grant targeting their tenant
        // (and only grants in their tenant). Verify both presence and scope.
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var grants = await response.Content.ReadFromJsonAsync<List<ImpersonationGrantPayload>>(Json);
        grants.ShouldNotBeNull();
        grants.ShouldAllBe(g => g.ImpersonatedTenantId == _tenantId);
        grants.ShouldContain(g => g.ImpersonatedUserId == _tenantAdminUserId);
    }

    [Fact]
    public async Task Revoke_Should_RejectImpersonationToken_OnSubsequentRequest()
    {
        // Arrange — impersonate inside the tenant, identify the grant, revoke.
        using var tenantClient = await CreateTenantAdminClientAsync();
        var impersonationToken = await StartImpersonationAsync(tenantClient, _tenantMemberUserId, _tenantId);
        var jti = ReadJti(impersonationToken);

        var grants = await tenantClient
            .GetFromJsonAsync<List<ImpersonationGrantPayload>>(
                $"{ImpersonationBasePath}/grants?Status=Active", Json);
        var targetGrant = grants!.First(g => g.Jti == jti);

        // Act — revoke, then try to use the impersonation token.
        var revokeResponse = await tenantClient.PostAsJsonAsync(
            $"{ImpersonationBasePath}/grants/{targetGrant.Id}/revoke",
            new { reason = "operator left for the day" });
        revokeResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        // Assert — the JWT validation hook should now 401 the impersonation token.
        // Cache TTL is short (and revoke primes the cache to EndedOrRevoked) so
        // the rejection should be effectively immediate.
        using var killedClient = ClientWithBearer(impersonationToken);
        var meResponse = await killedClient.GetAsync($"{TestConstants.IdentityBasePath}/profile");
        meResponse.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task End_Should_MarkGrant_As_Ended()
    {
        // Arrange
        using var tenantClient = await CreateTenantAdminClientAsync();
        var impersonationToken = await StartImpersonationAsync(tenantClient, _tenantMemberUserId, _tenantId);
        var jti = ReadJti(impersonationToken);

        // Act — end via the impersonation session, then list ended grants.
        using var endClient = ClientWithBearer(impersonationToken);
        var endResponse = await endClient.PostAsync($"{ImpersonationBasePath}/end", content: null);
        endResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        // Assert — the grant must show up in the Ended bucket.
        var ended = await tenantClient.GetFromJsonAsync<List<ImpersonationGrantPayload>>(
            $"{ImpersonationBasePath}/grants?Status=Ended", Json);
        ended.ShouldNotBeNull();
        ended.ShouldContain(g => g.Jti == jti);
    }

    [Fact]
    public async Task Revoke_Should_BeIdempotent_When_GrantAlreadyTerminal()
    {
        // Arrange — exchange, then revoke once.
        using var rootClient = await _auth.CreateRootAdminClientAsync();
        var acting = await ExchangeAsync(rootClient, _tenantId);

        var firstRevoke = await rootClient.PostAsJsonAsync(
            $"{ImpersonationBasePath}/grants/{acting.GrantId}/revoke",
            new { reason = "first call" });
        firstRevoke.StatusCode.ShouldBe(HttpStatusCode.OK);

        // Act — revoke again on the same grant.
        var secondRevoke = await rootClient.PostAsJsonAsync(
            $"{ImpersonationBasePath}/grants/{acting.GrantId}/revoke",
            new { reason = "second call" });

        // Assert — service treats already-terminal as a no-op and surfaces the
        // existing state; should not 5xx.
        secondRevoke.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Revoke_Should_Return404_When_GrantDoesNotExist()
    {
        // Arrange
        using var rootClient = await _auth.CreateRootAdminClientAsync();
        var bogusGuid = Guid.NewGuid();

        // Act
        var response = await rootClient.PostAsJsonAsync(
            $"{ImpersonationBasePath}/grants/{bogusGuid}/revoke",
            new { reason = "nothing here" });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    #endregion

    // ─── Permissions enforcement ────────────────────────────────────────

    #region Permissions

    [Fact]
    public async Task Start_Should_Return403_When_CallerLacksImpersonatePerm()
    {
        // Arrange — a freshly registered non-admin user only carries the Basic
        // role, which does NOT include Users.Impersonate.
        using var tenantAdminClient = await CreateTenantAdminClientAsync();
        var basic = await TenantFixture.RegisterAndConfirmUserAsync(
            _factory, tenantAdminClient, _tenantId, "basic");
        using var basicClient = await _auth.CreateAuthenticatedClientAsync(
            basic.Email, basic.Password, _tenantId);

        // Act
        var response = await basicClient.PostAsJsonAsync($"{ImpersonationBasePath}/start", new
        {
            targetUserId = _tenantAdminUserId,
            targetTenantId = _tenantId,
            reason = "basic user trying to impersonate",
        });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GetGrants_Should_Return403_When_CallerLacksViewPerm()
    {
        // Arrange
        using var tenantAdminClient = await CreateTenantAdminClientAsync();
        var basic = await TenantFixture.RegisterAndConfirmUserAsync(
            _factory, tenantAdminClient, _tenantId, "basicview");
        using var basicClient = await _auth.CreateAuthenticatedClientAsync(
            basic.Email, basic.Password, _tenantId);

        // Act
        var response = await basicClient.GetAsync($"{ImpersonationBasePath}/grants");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Revoke_Should_Return403_When_CallerLacksRevokePerm()
    {
        // Arrange — create a grant as root, then attempt revoke as a basic user
        // who doesn't hold Impersonation.Revoke.
        using var rootClient = await _auth.CreateRootAdminClientAsync();
        var acting = await ExchangeAsync(rootClient, _tenantId);

        using var tenantAdminClient = await CreateTenantAdminClientAsync();
        var basic = await TenantFixture.RegisterAndConfirmUserAsync(
            _factory, tenantAdminClient, _tenantId, "basicrev");
        using var basicClient = await _auth.CreateAuthenticatedClientAsync(
            basic.Email, basic.Password, _tenantId);

        // Act
        var response = await basicClient.PostAsJsonAsync(
            $"{ImpersonationBasePath}/grants/{acting.GrantId}/revoke",
            new { reason = "I shouldn't be able to do this" });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    #endregion

    // ─── Cross-tenant authorization on Revoke ──────────────────────────

    #region Cross-tenant revoke

    [Fact]
    public async Task Revoke_Should_Allow_TenantAdmin_When_GrantTargetsTheirTenant()
    {
        // Arrange — root exchanges into the test tenant.
        using var rootClient = await _auth.CreateRootAdminClientAsync();
        var acting = await ExchangeAsync(rootClient, _tenantId);

        // Act — the test tenant's admin revokes the grant targeting their tenant.
        using var tenantClient = await CreateTenantAdminClientAsync();
        var response = await tenantClient.PostAsJsonAsync(
            $"{ImpersonationBasePath}/grants/{acting.GrantId}/revoke",
            new { reason = "we noticed a session targeting our tenant" });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Revoke_Should_Return404_When_TenantAdmin_TargetsGrant_OutsideTheirTenant()
    {
        // Arrange — provision a second tenant and create a grant into THAT tenant. The first
        // tenant's admin must not be able to see or revoke it.
        var otherTenantId = $"impother-{Guid.NewGuid().ToString("N")[..8]}";
        var otherAdminEmail = $"otheradmin-{Guid.NewGuid().ToString("N")[..8]}@imptest.com";

        using var rootClient = await _auth.CreateRootAdminClientAsync();
        await TenantFixture.CreateTenantAsync(rootClient, otherTenantId, otherAdminEmail);
        await TenantFixture.WaitForProvisioningAsync(rootClient, otherTenantId);
        _ = await TenantFixture.GetTokenWithRetryAsync(
            _auth, otherAdminEmail, TestConstants.DefaultPassword, otherTenantId);

        var acting = await ExchangeAsync(rootClient, otherTenantId);

        // Act — the first test tenant's admin tries to revoke this cross-tenant grant.
        using var tenantClient = await CreateTenantAdminClientAsync();
        var response = await tenantClient.PostAsJsonAsync(
            $"{ImpersonationBasePath}/grants/{acting.GrantId}/revoke",
            new { reason = "fishing" });

        // Assert — handler returns NotFoundException (404) rather than 403 so
        // we don't confirm cross-tenant grant existence to outside callers.
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    #endregion

    // ─── Grant data fidelity ────────────────────────────────────────────

    #region Grant data

    [Fact]
    public async Task Grant_Should_PersistReason_And_ActorIdentity()
    {
        // Arrange
        const string reason = "Customer ticket #4821 — verifying ledger discrepancy";
        using var tenantClient = await CreateTenantAdminClientAsync();
        var startResponse = await tenantClient.PostAsJsonAsync($"{ImpersonationBasePath}/start", new
        {
            targetUserId = _tenantMemberUserId,
            targetTenantId = _tenantId,
            reason,
            durationMinutes = 15,
        });
        startResponse.EnsureSuccessStatusCode();

        // Act
        var grants = await tenantClient.GetFromJsonAsync<List<ImpersonationGrantPayload>>(
            $"{ImpersonationBasePath}/grants?Status=Active", Json);

        // Assert — reason text round-trips, actor + impersonated identities are
        // captured, and *Name fields are populated from the claims pipeline.
        grants.ShouldNotBeNull();
        var grant = grants.First(g => g.ImpersonatedUserId == _tenantMemberUserId && g.Reason == reason);
        grant.ActorUserId.ShouldBe(_tenantAdminUserId);
        grant.ActorTenantId.ShouldBe(_tenantId);
        grant.ImpersonatedTenantId.ShouldBe(_tenantId);
        grant.ActorUserName.ShouldNotBeNullOrWhiteSpace();
        grant.ImpersonatedUserName.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Grant_Jti_Should_Match_IssuedJwt()
    {
        // Arrange
        using var tenantClient = await CreateTenantAdminClientAsync();
        var token = await StartImpersonationAsync(tenantClient, _tenantMemberUserId, _tenantId);
        var jtiClaim = ReadJti(token);

        // Act — query active grants and find the one with this jti.
        var grants = await tenantClient.GetFromJsonAsync<List<ImpersonationGrantPayload>>(
            $"{ImpersonationBasePath}/grants?Status=Active", Json);

        // Assert — exactly one grant matches this jti, proving the token's jti equals the value
        // persisted in the grant row (the revocation hook keys off this).
        grants.ShouldNotBeNull();
        grants.Count(g => g.Jti == jtiClaim).ShouldBe(1);
    }

    [Fact]
    public async Task Multiple_Impersonations_Should_HaveUniqueJtis()
    {
        // Arrange — issue two impersonation tokens back to back.
        using var tenantClient = await CreateTenantAdminClientAsync();
        var firstToken = await StartImpersonationAsync(tenantClient, _tenantMemberUserId, _tenantId);
        var secondToken = await StartImpersonationAsync(tenantClient, _tenantMemberUserId, _tenantId);

        // Act
        var firstJti = ReadJti(firstToken);
        var secondJti = ReadJti(secondToken);

        // Assert — distinct jtis, distinct grant rows, both Active.
        firstJti.ShouldNotBe(secondJti);
        var active = await tenantClient.GetFromJsonAsync<List<ImpersonationGrantPayload>>(
            $"{ImpersonationBasePath}/grants?Status=Active", Json);
        active.ShouldNotBeNull();
        active.ShouldContain(g => g.Jti == firstJti);
        active.ShouldContain(g => g.Jti == secondJti);
    }

    [Fact]
    public async Task Revoking_One_Should_NotAffect_Other_Concurrent_Session()
    {
        // Arrange — two active impersonation sessions for the same target.
        using var tenantClient = await CreateTenantAdminClientAsync();
        var keepToken = await StartImpersonationAsync(tenantClient, _tenantMemberUserId, _tenantId);
        var killToken = await StartImpersonationAsync(tenantClient, _tenantMemberUserId, _tenantId);

        var killJti = ReadJti(killToken);
        var active = await tenantClient.GetFromJsonAsync<List<ImpersonationGrantPayload>>(
            $"{ImpersonationBasePath}/grants?Status=Active", Json);
        var killGrant = active!.First(g => g.Jti == killJti);

        // Act — revoke only the second grant.
        var revokeResponse = await tenantClient.PostAsJsonAsync(
            $"{ImpersonationBasePath}/grants/{killGrant.Id}/revoke",
            new { reason = "kill only this one" });
        revokeResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        // Assert — the OTHER session must still be valid.
        using var keepClient = ClientWithBearer(keepToken);
        var keepProfile = await keepClient.GetAsync($"{TestConstants.IdentityBasePath}/profile");
        keepProfile.StatusCode.ShouldBe(HttpStatusCode.OK);

        // And the revoked session must be rejected.
        using var killClient = ClientWithBearer(killToken);
        var killProfile = await killClient.GetAsync($"{TestConstants.IdentityBasePath}/profile");
        killProfile.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    #endregion

    // ─── Filter parameters ──────────────────────────────────────────────

    #region Filters

    [Fact]
    public async Task GetGrants_Should_FilterByStatus()
    {
        // Arrange — make sure we have at least one Active and one Revoked grant.
        using var tenantClient = await CreateTenantAdminClientAsync();
        var toRevokeToken = await StartImpersonationAsync(tenantClient, _tenantMemberUserId, _tenantId);
        var toRevokeJti = ReadJti(toRevokeToken);

        var seed = await tenantClient.GetFromJsonAsync<List<ImpersonationGrantPayload>>(
            $"{ImpersonationBasePath}/grants?Status=Active", Json);
        var toRevoke = seed!.First(g => g.Jti == toRevokeJti);
        await tenantClient.PostAsJsonAsync(
            $"{ImpersonationBasePath}/grants/{toRevoke.Id}/revoke",
            new { reason = "for the filter test" });

        // Start another so Active isn't empty.
        _ = await StartImpersonationAsync(tenantClient, _tenantMemberUserId, _tenantId);

        // Act
        var activeOnly = await tenantClient.GetFromJsonAsync<List<ImpersonationGrantPayload>>(
            $"{ImpersonationBasePath}/grants?Status=Active", Json);
        var revokedOnly = await tenantClient.GetFromJsonAsync<List<ImpersonationGrantPayload>>(
            $"{ImpersonationBasePath}/grants?Status=Revoked", Json);

        // Assert — each bucket is internally consistent, and the revoked grant
        // we just made appears under Revoked but not Active.
        activeOnly.ShouldNotBeNull();
        activeOnly.ShouldAllBe(g => g.Status == "Active");
        revokedOnly.ShouldNotBeNull();
        revokedOnly.ShouldAllBe(g => g.Status == "Revoked");
        revokedOnly.ShouldContain(g => g.Id == toRevoke.Id);
        activeOnly.ShouldNotContain(g => g.Id == toRevoke.Id);
    }

    [Fact]
    public async Task GetGrants_Should_FilterByActorUserId()
    {
        // Arrange — the tenant admin impersonates a member of their tenant.
        using var tenantClient = await CreateTenantAdminClientAsync();
        _ = await StartImpersonationAsync(tenantClient, _tenantMemberUserId, _tenantId);

        // Act — query for grants by an actor that has none (bogus userId).
        var bogusActorGrants = await tenantClient.GetFromJsonAsync<List<ImpersonationGrantPayload>>(
            $"{ImpersonationBasePath}/grants?ActorUserId={Guid.NewGuid()}", Json);
        var adminActorGrants = await tenantClient.GetFromJsonAsync<List<ImpersonationGrantPayload>>(
            $"{ImpersonationBasePath}/grants?ActorUserId={_tenantAdminUserId}", Json);

        // Assert — bogus actor returns empty; the admin actor query returns at
        // least the grant we just created and only grants by that actor.
        bogusActorGrants.ShouldNotBeNull();
        bogusActorGrants.ShouldBeEmpty();
        adminActorGrants.ShouldNotBeNull();
        adminActorGrants.ShouldNotBeEmpty();
        adminActorGrants.ShouldAllBe(g => g.ActorUserId == _tenantAdminUserId);
    }

    #endregion

    // ─── End-after-revoke race ──────────────────────────────────────────

    #region End semantics after revoke

    [Fact]
    public async Task End_Should_Reject_When_GrantWasRevoked_First()
    {
        // Arrange — start, revoke, then try to End from the (now-dead) impersonation session.
        using var tenantClient = await CreateTenantAdminClientAsync();
        var impersonationToken = await StartImpersonationAsync(tenantClient, _tenantMemberUserId, _tenantId);
        var jti = ReadJti(impersonationToken);

        var grants = await tenantClient.GetFromJsonAsync<List<ImpersonationGrantPayload>>(
            $"{ImpersonationBasePath}/grants?Status=Active", Json);
        var grantId = grants!.First(g => g.Jti == jti).Id;

        await tenantClient.PostAsJsonAsync(
            $"{ImpersonationBasePath}/grants/{grantId}/revoke",
            new { reason = "killed before End" });

        using var deadClient = ClientWithBearer(impersonationToken);

        // Act
        var response = await deadClient.PostAsync($"{ImpersonationBasePath}/end", content: null);

        // Assert — the JWT validation hook short-circuits BEFORE the End handler
        // runs, so the caller sees 401, not a successful end.
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    #endregion

    // ─── helpers ────────────────────────────────────────────────────────

    private static async Task<string> StartImpersonationAsync(HttpClient asClient, string targetUserId, string targetTenantId)
    {
        var response = await asClient.PostAsJsonAsync($"{ImpersonationBasePath}/start", new
        {
            targetUserId,
            targetTenantId,
            reason = "test fixture",
        });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<ImpersonationResponse>(Json);
        return body!.AccessToken;
    }

    /// <summary>
    /// The only way to obtain a cross-tenant grant since #9: the root operator token exchange.
    /// Same issuer, same grant table, same revocation list — which is what these tests assert on.
    /// </summary>
    private static async Task<ExchangePayload> ExchangeAsync(HttpClient rootClient, string targetTenantId)
    {
        var response = await rootClient.PostAsJsonAsync(ExchangePath, new
        {
            targetTenantId,
            reason = "test fixture",
        });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ExchangePayload>(Json))!;
    }

    // No tenant argument: the token's `tenant` claim is the only thing that scopes the request.
    private HttpClient ClientWithBearer(string accessToken)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return client;
    }

    private Task<HttpClient> CreateTenantAdminClientAsync() =>
        _auth.CreateAuthenticatedClientAsync(_tenantAdminEmail, TestConstants.DefaultPassword, _tenantId);

    private static string ReadSubject(string accessToken) =>
        new JwtSecurityTokenHandler().ReadJwtToken(accessToken).Subject;

    private static string ReadJti(string accessToken) =>
        new JwtSecurityTokenHandler().ReadJwtToken(accessToken).Claims.First(c => c.Type == "jti").Value;

    // ─── shape mirrors ─────────────────────────────────────────────────

    private sealed class ImpersonationResponse
    {
        public string AccessToken { get; set; } = default!;
        public DateTime AccessTokenExpiresAt { get; set; }
        public string ActorUserId { get; set; } = default!;
        public string ActorTenantId { get; set; } = default!;
        public string ImpersonatedUserId { get; set; } = default!;
        public string ImpersonatedTenantId { get; set; } = default!;
    }

    private sealed class EndImpersonationPayload
    {
        public string ActorUserId { get; set; } = default!;
        public string ActorTenantId { get; set; } = default!;
        public string ImpersonatedUserId { get; set; } = default!;
        public string ImpersonatedTenantId { get; set; } = default!;
        public DateTime EndedAtUtc { get; set; }
    }

    private sealed class ExchangePayload
    {
        public string AccessToken { get; set; } = default!;
        public Guid GrantId { get; set; }
        public string Jti { get; set; } = default!;
    }

    private sealed class ImpersonationGrantPayload
    {
        public Guid Id { get; set; }
        public string Jti { get; set; } = default!;
        public string ActorUserId { get; set; } = default!;
        public string? ActorUserName { get; set; }
        public string ActorTenantId { get; set; } = default!;
        public string ImpersonatedUserId { get; set; } = default!;
        public string? ImpersonatedUserName { get; set; }
        public string ImpersonatedTenantId { get; set; } = default!;
        public string Reason { get; set; } = default!;
        public string Status { get; set; } = default!;
    }
}
