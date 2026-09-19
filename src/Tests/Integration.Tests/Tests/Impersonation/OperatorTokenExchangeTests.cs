// Test-only DTOs are populated by System.Text.Json via reflection — the SonarAnalyzer can't see
// the assignments and warns about "unused" private setters. Suppressed file-wide.
#pragma warning disable S1144 // Unused private types or members should be removed
#pragma warning disable S3459 // Unassigned members should be removed
using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Boilerplate.Modules.Auditing.Contracts;
using Integration.Tests.Infrastructure;
using Integration.Tests.Tests.Auditing;

namespace Integration.Tests.Tests.Impersonation;

/// <summary>
/// ADR-0002's cross-tenant mechanism end to end: a root operator exchanges their own token for a
/// short-lived, access-only token that acts inside another tenant.
///
/// The invariants under test are the ones that make this safe to ship: only root gets in, the
/// issued token sees exactly one tenant (the target), its lifetime is bounded by configuration
/// rather than by the caller, every exchange leaves an audit row joined to a revocable grant, and
/// revoking that grant kills the token on its next request.
/// </summary>
[Collection(AppCollectionDefinition.Name)]
public sealed class OperatorTokenExchangeTests : IAsyncLifetime
{
    private const string ExchangePath = TestConstants.IdentityBasePath + "/operator/token-exchange";
    private const string ImpersonationBasePath = TestConstants.IdentityBasePath + "/impersonation";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly AppWebApplicationFactory _factory;
    private readonly AuthHelper _auth;

    private string _tenantId = default!;
    private string _tenantAdminEmail = default!;
    private string _tenantAdminUserId = default!;
    private string _rootAdminUserId = default!;

    public OperatorTokenExchangeTests(AppWebApplicationFactory factory)
    {
        _factory = factory;
        _auth = new AuthHelper(factory);
    }

    public async Task InitializeAsync()
    {
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        _tenantId = $"xchg-{uniqueId}";
        _tenantAdminEmail = $"admin-{uniqueId}@xchg.com";

        using var rootClient = await _auth.CreateRootAdminClientAsync();
        await TenantFixture.CreateTenantAsync(rootClient, _tenantId, _tenantAdminEmail);
        await TenantFixture.WaitForProvisioningAsync(rootClient, _tenantId);

        var tenantToken = await TenantFixture.GetTokenWithRetryAsync(
            _auth, _tenantAdminEmail, TestConstants.DefaultPassword, _tenantId);
        _tenantAdminUserId = ReadSubject(tenantToken.AccessToken);

        var rootToken = await _auth.GetRootAdminTokenAsync();
        _rootAdminUserId = ReadSubject(rootToken.AccessToken);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ─── Happy path ─────────────────────────────────────────────────────

    #region Happy path

    [Fact]
    public async Task Exchange_Should_ActAsTenantAdmin_When_TargetUserOmitted()
    {
        // Arrange
        using var rootClient = await _auth.CreateRootAdminClientAsync();

        // Act — no targetUserId: the tenant's own admin (from the tenant record's AdminEmail) is the subject.
        var response = await rootClient.PostAsJsonAsync(ExchangePath, new
        {
            targetTenantId = _tenantId,
            reason = "customer ticket #1201 — checking their branding",
        });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ExchangeResponse>(Json);
        body.ShouldNotBeNull();
        body.TargetTenantId.ShouldBe(_tenantId);
        body.TargetUserId.ShouldBe(_tenantAdminUserId);
        body.ActorUserId.ShouldBe(_rootAdminUserId);
        body.ActorTenantId.ShouldBe(TestConstants.RootTenantId);
        body.Jti.ShouldNotBeNullOrWhiteSpace();
        body.GrantId.ShouldNotBe(Guid.Empty);

        // One token, one tenant: the issued token names the TARGET tenant and carries the operator
        // in act_* so every downstream audit/permission decision knows who is really acting.
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(body.AccessToken);
        jwt.Subject.ShouldBe(_tenantAdminUserId);
        jwt.Claims.ShouldContain(c => c.Type == "tenant" && c.Value == _tenantId);
        jwt.Claims.ShouldContain(c => c.Type == "act_sub" && c.Value == _rootAdminUserId);
        jwt.Claims.ShouldContain(c => c.Type == "act_tenant" && c.Value == TestConstants.RootTenantId);
        jwt.Claims.First(c => c.Type == "jti").Value.ShouldBe(body.Jti);
    }

    [Fact]
    public async Task Exchange_Should_ActAsNamedUser_When_TargetUserSupplied()
    {
        // Arrange — a plain member of the target tenant, not its admin.
        using var rootClient = await _auth.CreateRootAdminClientAsync();
        using var tenantAdminClient = await CreateTenantAdminClientAsync();
        var member = await TenantFixture.RegisterAndConfirmUserAsync(
            _factory, tenantAdminClient, _tenantId, "member");

        // Act
        var response = await rootClient.PostAsJsonAsync(ExchangePath, new
        {
            targetTenantId = _tenantId,
            targetUserId = member.UserId,
            reason = "reproducing a member-only bug",
        });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ExchangeResponse>(Json);
        body!.TargetUserId.ShouldBe(member.UserId);
        new JwtSecurityTokenHandler().ReadJwtToken(body.AccessToken).Subject.ShouldBe(member.UserId);
    }

    [Fact]
    public async Task Exchange_Should_IssueNoRefreshTokenCookieOrSession()
    {
        // Arrange — count the target admin's sessions before the exchange.
        using var rootClient = await _auth.CreateRootAdminClientAsync();
        using var tenantAdminClient = await CreateTenantAdminClientAsync();
        var sessionsBefore = await CountSessionsAsync(tenantAdminClient);

        // Act
        var response = await rootClient.PostAsJsonAsync(ExchangePath, new
        {
            targetTenantId = _tenantId,
            reason = "no session must appear in the target tenant",
        });
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        // Assert — access-only on every axis: no refresh token in the body, no refresh cookie, and
        // no new row in the target tenant's session store (nothing to rotate, nothing to leak).
        var raw = await response.Content.ReadAsStringAsync();
        raw.ShouldNotContain("refreshToken", Case.Insensitive);
        response.Headers.Contains("Set-Cookie").ShouldBeFalse();

        var sessionsAfter = await CountSessionsAsync(tenantAdminClient);
        sessionsAfter.ShouldBe(sessionsBefore);
    }

    #endregion

    // ─── Lifetime ───────────────────────────────────────────────────────

    #region Lifetime

    [Fact]
    public async Task Exchange_Should_ClampLifetime_To_ConfiguredMaximum()
    {
        // Arrange
        using var rootClient = await _auth.CreateRootAdminClientAsync();

        // Act — ask for a full day; the server clamps instead of failing.
        var response = await rootClient.PostAsJsonAsync(ExchangePath, new
        {
            targetTenantId = _tenantId,
            reason = "trying to hold the door open",
            durationMinutes = 1440,
        });

        // Assert — exp - iat must never exceed the configured ceiling.
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ExchangeResponse>(Json);
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(body!.AccessToken);
        var lifetime = jwt.ValidTo - jwt.ValidFrom;
        lifetime.ShouldBeLessThanOrEqualTo(TimeSpan.FromMinutes(TestConstants.OperatorExchangeMaxMinutes));
    }

    #endregion

    // ─── Authorization ──────────────────────────────────────────────────

    #region Authorization

    [Fact]
    public async Task Exchange_Should_Return403_When_CallerIsTenantAdmin()
    {
        // Arrange — a tenant admin is not a platform operator, even for their own tenant.
        using var tenantClient = await CreateTenantAdminClientAsync();

        // Act
        var response = await tenantClient.PostAsJsonAsync(ExchangePath, new
        {
            targetTenantId = _tenantId,
            reason = "tenant admin should not reach this door",
        });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Exchange_Should_Return403_When_RootCallerLacksThePermission()
    {
        // Arrange — a fresh user inside the ROOT tenant carries only the Basic role, which does
        // not include the root-only Platform.Users.Impersonate permission.
        using var rootClient = await _auth.CreateRootAdminClientAsync();
        var basic = await TenantFixture.RegisterAndConfirmUserAsync(
            _factory, rootClient, TestConstants.RootTenantId, "basicxchg");
        using var basicClient = await _auth.CreateAuthenticatedClientAsync(
            basic.Email, basic.Password, TestConstants.RootTenantId);

        // Act
        var response = await basicClient.PostAsJsonAsync(ExchangePath, new
        {
            targetTenantId = _tenantId,
            reason = "root tenant, wrong role",
        });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Exchange_Should_Reject_When_CallerIsAlreadyActing()
    {
        // Arrange — no nesting: an exchanged token may not be exchanged again, or act_* would be
        // overwritten and the audit trail would lose the real operator.
        using var rootClient = await _auth.CreateRootAdminClientAsync();
        var acting = await ExchangeAsync(rootClient, _tenantId);
        using var actingClient = ClientWithBearer(acting.AccessToken);

        // Act
        var response = await actingClient.PostAsJsonAsync(ExchangePath, new
        {
            targetTenantId = _tenantId,
            reason = "nesting attempt",
        });

        // Assert
        response.IsSuccessStatusCode.ShouldBeFalse();
        ((int)response.StatusCode).ShouldBeInRange(400, 499);
    }

    [Fact]
    public async Task Exchange_Should_Reject_ImpersonationStart_From_ActingToken()
    {
        // Arrange — the same no-nesting rule from the impersonation side.
        using var rootClient = await _auth.CreateRootAdminClientAsync();
        var acting = await ExchangeAsync(rootClient, _tenantId);
        using var actingClient = ClientWithBearer(acting.AccessToken);

        // Act
        var response = await actingClient.PostAsJsonAsync($"{ImpersonationBasePath}/start", new
        {
            targetUserId = _tenantAdminUserId,
            targetTenantId = _tenantId,
            reason = "nested impersonation",
        });

        // Assert
        response.IsSuccessStatusCode.ShouldBeFalse();
        ((int)response.StatusCode).ShouldBeInRange(400, 499);
    }

    [Fact]
    public async Task Exchange_Should_Return401_When_Anonymous()
    {
        // Arrange
        using var anonClient = _factory.CreateClient();

        // Act
        var response = await anonClient.PostAsJsonAsync(ExchangePath, new
        {
            targetTenantId = _tenantId,
            reason = "anonymous",
        });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    #endregion

    // ─── Tenant scoping of the issued token ─────────────────────────────

    #region Scope

    [Fact]
    public async Task ExchangedToken_Should_SeeOnlyTheTargetTenantsUsers()
    {
        // Arrange — a third tenant exists whose users must stay invisible.
        var otherTenantId = $"xchgother-{Guid.NewGuid().ToString("N")[..8]}";
        var otherAdminEmail = $"otheradmin-{Guid.NewGuid().ToString("N")[..8]}@xchg.com";
        using var rootClient = await _auth.CreateRootAdminClientAsync();
        await TenantFixture.CreateTenantAsync(rootClient, otherTenantId, otherAdminEmail);
        await TenantFixture.WaitForProvisioningAsync(rootClient, otherTenantId);
        var otherToken = await TenantFixture.GetTokenWithRetryAsync(
            _auth, otherAdminEmail, TestConstants.DefaultPassword, otherTenantId);
        var otherAdminUserId = ReadSubject(otherToken.AccessToken);

        var acting = await ExchangeAsync(rootClient, _tenantId);
        using var actingClient = ClientWithBearer(acting.AccessToken);

        // Act
        var users = await actingClient.GetFromJsonAsync<List<UserPayload>>(
            $"{TestConstants.IdentityBasePath}/users", Json);

        // Assert — exactly one tenant is visible: the target's.
        users.ShouldNotBeNull();
        users.ShouldContain(u => u.Id == _tenantAdminUserId);
        users.ShouldNotContain(u => u.Id == _rootAdminUserId);
        users.ShouldNotContain(u => u.Id == otherAdminUserId);
    }

    [Fact]
    public async Task ExchangedToken_Should_IgnoreForgedTenantHeader()
    {
        // Arrange — ADR-0002: the tenant comes from the signed claim, never from the wire.
        using var rootClient = await _auth.CreateRootAdminClientAsync();
        var acting = await ExchangeAsync(rootClient, _tenantId);

        using var forgedClient = ClientWithBearer(acting.AccessToken);
        forgedClient.DefaultRequestHeaders.Add("tenant", TestConstants.RootTenantId);

        // Act
        var users = await forgedClient.GetFromJsonAsync<List<UserPayload>>(
            $"{TestConstants.IdentityBasePath}/users", Json);

        // Assert — still the target tenant's users, header or no header.
        users.ShouldNotBeNull();
        users.ShouldContain(u => u.Id == _tenantAdminUserId);
        users.ShouldNotContain(u => u.Id == _rootAdminUserId);
    }

    [Fact]
    public async Task ExchangedToken_Should_ReadTheTargetTenantsTheme()
    {
        // Arrange — the branding editor the admin app re-enables while acting: the theme endpoints
        // are current-tenant scoped, and while acting the current tenant IS the target.
        using var rootClient = await _auth.CreateRootAdminClientAsync();
        var acting = await ExchangeAsync(rootClient, _tenantId);
        using var actingClient = ClientWithBearer(acting.AccessToken);

        // Act
        var response = await actingClient.GetAsync($"{TestConstants.TenantsBasePath}/theme");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    #endregion

    // ─── Target resolution failures ─────────────────────────────────────

    #region Target resolution

    [Fact]
    public async Task Exchange_Should_Return404_When_TargetTenantIsUnknown()
    {
        // Arrange
        using var rootClient = await _auth.CreateRootAdminClientAsync();

        // Act
        var response = await rootClient.PostAsJsonAsync(ExchangePath, new
        {
            targetTenantId = $"ghost-{Guid.NewGuid():N}",
            reason = "tenant does not exist",
        });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Exchange_Should_Return403_When_TargetTenantIsDeactivated()
    {
        // Arrange — provision a tenant, then deactivate it.
        var deadTenantId = $"xchgdead-{Guid.NewGuid().ToString("N")[..8]}";
        var deadAdminEmail = $"deadadmin-{Guid.NewGuid().ToString("N")[..8]}@xchg.com";
        using var rootClient = await _auth.CreateRootAdminClientAsync();
        await TenantFixture.CreateTenantAsync(rootClient, deadTenantId, deadAdminEmail);
        await TenantFixture.WaitForProvisioningAsync(rootClient, deadTenantId);

        var deactivate = await rootClient.PostAsJsonAsync(
            $"{TestConstants.TenantsBasePath}/{deadTenantId}/activation",
            new { tenantId = deadTenantId, isActive = false });
        deactivate.EnsureSuccessStatusCode();

        // Act
        var response = await rootClient.PostAsJsonAsync(ExchangePath, new
        {
            targetTenantId = deadTenantId,
            reason = "entering a deactivated tenant",
        });

        // Assert — refused, not silently issued: the deactivated-tenant guard would reject every
        // request made with such a token (its resolved tenant is the target), so the token would
        // be dead on arrival. Reactivate the tenant to inspect it.
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Exchange_Should_Return404_When_TargetUserIsNotInTheTargetTenant()
    {
        // Arrange — the root admin's user id does not exist inside the test tenant.
        using var rootClient = await _auth.CreateRootAdminClientAsync();

        // Act
        var response = await rootClient.PostAsJsonAsync(ExchangePath, new
        {
            targetTenantId = _tenantId,
            targetUserId = _rootAdminUserId,
            reason = "wrong-tenant user id",
        });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Exchange_Should_Return400_When_ReasonIsMissing()
    {
        // Arrange
        using var rootClient = await _auth.CreateRootAdminClientAsync();

        // Act
        var response = await rootClient.PostAsJsonAsync(ExchangePath, new
        {
            targetTenantId = _tenantId,
        });

        // Assert — the reason is what makes the audit row worth reading.
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Exchange_Should_Reject_When_TargetTenantIsRoot()
    {
        // Arrange
        using var rootClient = await _auth.CreateRootAdminClientAsync();

        // Act
        var response = await rootClient.PostAsJsonAsync(ExchangePath, new
        {
            targetTenantId = TestConstants.RootTenantId,
            reason = "already here",
        });

        // Assert
        response.IsSuccessStatusCode.ShouldBeFalse();
        ((int)response.StatusCode).ShouldBeInRange(400, 499);
    }

    #endregion

    // ─── Audit + revocation ─────────────────────────────────────────────

    #region Audit and revocation

    [Fact]
    public async Task Exchange_Should_WriteSecurityAudit_InTheOperatorsTenant()
    {
        // Arrange
        const string reason = "Customer ticket #7788 — exchange audit trail";
        using var rootClient = await _auth.CreateRootAdminClientAsync();

        // Act
        var acting = await ExchangeAsync(rootClient, _tenantId, reason: reason);

        // Assert — the row lands in the ambient tenant of the *caller*, i.e. root, so operators can
        // find their own crossings; it names the operator, the target and the jti of the grant.
        var summary = await AuditTestHelper.PollForAuditAsync(
            rootClient,
            a => a.EventType == AuditEventType.Security
                 && a.TenantId == TestConstants.RootTenantId
                 && a.UserId == _rootAdminUserId);

        var detail = await AuditTestHelper.GetByIdAsync(rootClient, summary.Id);
        detail.ShouldNotBeNull();

        // Find the specific exchange row by jti (several security rows exist per run).
        var match = await PollForSecurityAuditWithJtiAsync(rootClient, acting.Jti);
        var payload = match.Payload.GetRawText();
        payload.ShouldContain(acting.Jti);
        payload.ShouldContain(reason);
        payload.ShouldContain(_tenantId);
        payload.ShouldContain(_tenantAdminUserId);
    }

    [Fact]
    public async Task ExchangedToken_Should_BeRejected_After_ItsGrantIsRevoked()
    {
        // Arrange
        using var rootClient = await _auth.CreateRootAdminClientAsync();
        var acting = await ExchangeAsync(rootClient, _tenantId);
        using var actingClient = ClientWithBearer(acting.AccessToken);

        // Sanity: the token works before the revoke (this also warms the grant cache, so the
        // assertion afterwards proves the revoke invalidates the cached state, not just the row).
        var before = await actingClient.GetAsync($"{TestConstants.IdentityBasePath}/profile");
        before.StatusCode.ShouldBe(HttpStatusCode.OK);

        // Act — revoke the grant the exchange created.
        var revoke = await rootClient.PostAsJsonAsync(
            $"{ImpersonationBasePath}/grants/{acting.GrantId}/revoke",
            new { reason = "operator left for the day" });
        revoke.StatusCode.ShouldBe(HttpStatusCode.OK);

        // Assert — the JWT validation hook rejects the next request outright.
        var after = await actingClient.GetAsync($"{TestConstants.IdentityBasePath}/profile");
        after.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Exchange_Should_AppearAsGrant_InTheUnifiedGrantList()
    {
        // Arrange — one grant table for impersonation and exchange alike.
        using var rootClient = await _auth.CreateRootAdminClientAsync();
        var acting = await ExchangeAsync(rootClient, _tenantId, reason: "grant list check");

        // Act
        var grants = await rootClient.GetFromJsonAsync<List<GrantPayload>>(
            $"{ImpersonationBasePath}/grants?Status=Active&ImpersonatedTenantId={_tenantId}", Json);

        // Assert
        grants.ShouldNotBeNull();
        var grant = grants.Where(g => g.Jti == acting.Jti).ShouldHaveSingleItem();
        grant.Id.ShouldBe(acting.GrantId);
        grant.ActorUserId.ShouldBe(_rootAdminUserId);
        grant.ActorTenantId.ShouldBe(TestConstants.RootTenantId);
        grant.ImpersonatedTenantId.ShouldBe(_tenantId);
        grant.Reason.ShouldBe("grant list check");
    }

    [Fact]
    public async Task End_Should_ReturnNoToken_And_KillTheActingToken()
    {
        // Arrange — "exit tenant": the operator keeps their own session, so End hands back nothing
        // to install; it just ends the grant.
        using var rootClient = await _auth.CreateRootAdminClientAsync();
        var acting = await ExchangeAsync(rootClient, _tenantId);
        using var actingClient = ClientWithBearer(acting.AccessToken);

        // Act
        var response = await actingClient.PostAsync($"{ImpersonationBasePath}/end", content: null);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var raw = await response.Content.ReadAsStringAsync();
        raw.ShouldNotContain("accessToken", Case.Insensitive);
        raw.ShouldNotContain("refreshToken", Case.Insensitive);

        var ended = await response.Content.ReadFromJsonAsync<EndPayload>(Json);
        ended!.ActorUserId.ShouldBe(_rootAdminUserId);
        ended.ImpersonatedTenantId.ShouldBe(_tenantId);

        // …and the acting token is dead on its next request.
        var after = await actingClient.GetAsync($"{TestConstants.IdentityBasePath}/profile");
        after.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    #endregion

    // ─── helpers ────────────────────────────────────────────────────────

    private static async Task<ExchangeResponse> ExchangeAsync(
        HttpClient asClient,
        string targetTenantId,
        string? targetUserId = null,
        string reason = "integration test fixture")
    {
        var response = await asClient.PostAsJsonAsync(ExchangePath, new
        {
            targetTenantId,
            targetUserId,
            reason,
        });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ExchangeResponse>(Json))!;
    }

    private static async Task<int> CountSessionsAsync(HttpClient client)
    {
        var sessions = await client.GetFromJsonAsync<List<SessionPayload>>(
            $"{TestConstants.IdentityBasePath}/sessions/me", Json);
        return sessions?.Count ?? 0;
    }

    private static async Task<AuditDetail> PollForSecurityAuditWithJtiAsync(HttpClient client, string jti)
    {
        for (var i = 0; i < AuditTestHelper.DefaultPollAttempts; i++)
        {
            var page = await AuditTestHelper.GetAuditsPageAsync(client, pageSize: 100);
            foreach (var row in page.Items.Where(r => r.EventType == AuditEventType.Security))
            {
                var detail = await AuditTestHelper.GetByIdAsync(client, row.Id);
                if (detail is not null && detail.Payload.GetRawText().Contains(jti, StringComparison.Ordinal))
                {
                    return new AuditDetail(detail.Payload);
                }
            }
            await Task.Delay(AuditTestHelper.PollInterval);
        }

        throw new TimeoutException($"No security audit row carried jti={jti}.");
    }

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

    // ─── shape mirrors ──────────────────────────────────────────────────

    private sealed record AuditDetail(JsonElement Payload);

    private sealed class ExchangeResponse
    {
        public string AccessToken { get; set; } = default!;
        public DateTime AccessTokenExpiresAt { get; set; }
        public string TargetTenantId { get; set; } = default!;
        public string TargetUserId { get; set; } = default!;
        public string? TargetUserName { get; set; }
        public string ActorUserId { get; set; } = default!;
        public string ActorTenantId { get; set; } = default!;
        public Guid GrantId { get; set; }
        public string Jti { get; set; } = default!;
    }

    private sealed class EndPayload
    {
        public string ActorUserId { get; set; } = default!;
        public string ActorTenantId { get; set; } = default!;
        public string ImpersonatedUserId { get; set; } = default!;
        public string ImpersonatedTenantId { get; set; } = default!;
        public DateTime EndedAtUtc { get; set; }
    }

    private sealed class GrantPayload
    {
        public Guid Id { get; set; }
        public string Jti { get; set; } = default!;
        public string ActorUserId { get; set; } = default!;
        public string ActorTenantId { get; set; } = default!;
        public string ImpersonatedUserId { get; set; } = default!;
        public string ImpersonatedTenantId { get; set; } = default!;
        public string Reason { get; set; } = default!;
        public string Status { get; set; } = default!;
    }

    private sealed class UserPayload
    {
        public string Id { get; set; } = default!;
        public string? Email { get; set; }
    }

    private sealed class SessionPayload
    {
        public Guid Id { get; set; }
    }
}
