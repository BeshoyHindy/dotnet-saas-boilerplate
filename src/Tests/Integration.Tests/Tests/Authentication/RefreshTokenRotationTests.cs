using System.IdentityModel.Tokens.Jwt;
using Integration.Tests.Infrastructure;
using Integration.Tests.Tests.Sessions;

namespace Integration.Tests.Tests.Authentication;

/// <summary>
/// The refresh-token contract of ADR-0002: one tenant-bound <c>UserSession</c> row per device,
/// an opaque <c>"{tenantId}.{32 CSPRNG bytes}"</c> token stored only as a SHA-256 hash, and a
/// rotation that is a single compare-and-set so concurrency cannot mint two live tokens from one.
/// </summary>
[Collection(AppCollectionDefinition.Name)]
public sealed class RefreshTokenRotationTests
{
    private readonly AppWebApplicationFactory _factory;
    private readonly AuthHelper _auth;

    public RefreshTokenRotationTests(AppWebApplicationFactory factory)
    {
        _factory = factory;
        _auth = new AuthHelper(factory);
    }

    [Fact]
    public async Task RefreshToken_Should_CarryTenantPrefix_And_BeOpaque()
    {
        var token = await _auth.GetRootAdminTokenAsync();

        token.RefreshToken.ShouldStartWith($"{TestConstants.RootTenantId}.");

        // 32 CSPRNG bytes, base64url, unpadded → 43 characters.
        var secret = token.RefreshToken[(TestConstants.RootTenantId.Length + 1)..];
        secret.Length.ShouldBe(43);
        secret.ShouldNotContain("+");
        secret.ShouldNotContain("/");
        secret.ShouldNotContain("=");
    }

    [Fact]
    public async Task ConcurrentRefresh_Should_LetExactlyOneCallerWin()
    {
        // Arrange — one session, one refresh token, N callers racing to rotate it.
        const int callers = 8;
        var token = await _auth.GetRootAdminTokenAsync();

        // Act
        var responses = await Task.WhenAll(Enumerable
            .Range(0, callers)
            .Select(_ => RefreshAsync(TestConstants.RootTenantId, token.AccessToken, token.RefreshToken)));

        // Assert — the compare-and-set admits exactly one winner; the rest are unauthorized.
        try
        {
            responses.Count(r => r.StatusCode == HttpStatusCode.OK).ShouldBe(1);
            responses.Count(r => r.StatusCode == HttpStatusCode.Unauthorized).ShouldBe(callers - 1);
        }
        finally
        {
            foreach (var response in responses) response.Dispose();
        }
    }

    [Fact]
    public async Task ReplayOfRotatedToken_Should_RevokeTheWholeSession()
    {
        // Arrange — rotate once, so the original token becomes the session's PreviousTokenHash.
        var token = await _auth.GetRootAdminTokenAsync();
        using var first = await RefreshAsync(TestConstants.RootTenantId, token.AccessToken, token.RefreshToken);
        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        var rotated = await first.Content.ReadFromJsonAsync<TokenRefreshResult>();
        rotated.ShouldNotBeNull();

        // Act — an attacker replays the token that was already spent.
        using var replay = await RefreshAsync(TestConstants.RootTenantId, token.AccessToken, token.RefreshToken);

        // Assert — the replay fails AND burns the session, so the legitimate holder's
        // freshly-rotated token stops working too (reuse detection, RFC 9700 §4.14.2).
        replay.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        using var afterReuse = await RefreshAsync(TestConstants.RootTenantId, rotated.Token, rotated.RefreshToken);
        afterReuse.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task SidClaim_Should_SurviveRotation()
    {
        var token = await _auth.GetRootAdminTokenAsync();
        var originalSid = SidOf(token.AccessToken);
        originalSid.ShouldNotBeNullOrWhiteSpace();

        using var response = await RefreshAsync(TestConstants.RootTenantId, token.AccessToken, token.RefreshToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var rotated = await response.Content.ReadFromJsonAsync<TokenRefreshResult>();

        // The device keeps its identity across rotation: same row, same sid.
        SidOf(rotated!.Token).ShouldBe(originalSid);
    }

    [Fact]
    public async Task TwoDevices_Should_RefreshIndependently()
    {
        // Arrange — the same user signs in twice; each login is its own session row.
        using var adminClient = await _auth.CreateRootAdminClientAsync();
        var user = await IdentityUserSeeder.CreateLoginableUserAsync(_factory, adminClient, "two-device");

        var deviceA = await _auth.GetTokenAsync(user.Email, user.Password);
        var deviceB = await _auth.GetTokenAsync(user.Email, user.Password);

        deviceA.RefreshToken.ShouldNotBe(deviceB.RefreshToken);
        SidOf(deviceA.AccessToken).ShouldNotBe(SidOf(deviceB.AccessToken));

        // Act — device A rotates twice; device B has not moved.
        using var firstA = await RefreshAsync(TestConstants.RootTenantId, deviceA.AccessToken, deviceA.RefreshToken);
        firstA.StatusCode.ShouldBe(HttpStatusCode.OK);
        var rotatedA = await firstA.Content.ReadFromJsonAsync<TokenRefreshResult>();
        using var secondA = await RefreshAsync(TestConstants.RootTenantId, rotatedA!.Token, rotatedA.RefreshToken);
        secondA.StatusCode.ShouldBe(HttpStatusCode.OK);

        // Assert — device B is untouched by everything device A did.
        using var b = await RefreshAsync(TestConstants.RootTenantId, deviceB.AccessToken, deviceB.RefreshToken);
        b.StatusCode.ShouldBe(HttpStatusCode.OK);
        var rotatedB = await b.Content.ReadFromJsonAsync<TokenRefreshResult>();
        SidOf(rotatedB!.Token).ShouldBe(SidOf(deviceB.AccessToken));
    }

    [Fact]
    public async Task PasswordChange_Should_KillExistingSessions()
    {
        // Arrange
        using var adminClient = await _auth.CreateRootAdminClientAsync();
        var user = await IdentityUserSeeder.CreateLoginableUserAsync(_factory, adminClient, "stamp-kill");
        var token = await _auth.GetTokenAsync(user.Email, user.Password);

        using var userClient = _factory.CreateClient();
        userClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);

        const string newPassword = "Test@5678!";
        var change = await userClient.PostAsJsonAsync($"{TestConstants.IdentityBasePath}/change-password", new
        {
            password = user.Password,
            newPassword,
            confirmNewPassword = newPassword
        });
        change.StatusCode.ShouldBe(HttpStatusCode.OK);

        // Act — the session was minted against the old security stamp.
        using var response = await RefreshAsync(TestConstants.RootTenantId, token.AccessToken, token.RefreshToken);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task RefreshToken_Should_Fail_When_PresentedUnderAnotherTenantsPrefix()
    {
        // Arrange — a second, fully provisioned tenant to aim the token at.
        using var rootClient = await _auth.CreateRootAdminClientAsync();
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var otherTenantId = $"refresh-iso-{uniqueId}";
        await CreateTenantAsync(rootClient, otherTenantId, $"refresh-iso-admin-{uniqueId}@tenant.com");
        await WaitForProvisioningAsync(rootClient, otherTenantId);

        var rootToken = await _auth.GetRootAdminTokenAsync();
        var secret = rootToken.RefreshToken[(TestConstants.RootTenantId.Length + 1)..];

        // Act — (1) the whole root token posted to the other tenant's refresh route,
        //       (2) root's secret re-labelled with the other tenant's prefix.
        using var wrongRoute = await RefreshAsync(otherTenantId, rootToken.AccessToken, rootToken.RefreshToken);
        using var forgedPrefix = await RefreshAsync(otherTenantId, rootToken.AccessToken, $"{otherTenantId}.{secret}");

        // Assert
        wrongRoute.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        forgedPrefix.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // And the legitimate holder is untouched by the failed cross-tenant attempts.
        using var legitimate = await RefreshAsync(TestConstants.RootTenantId, rootToken.AccessToken, rootToken.RefreshToken);
        legitimate.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Login_Should_SetRefreshCookie_ScopedToTheRefreshPath()
    {
        using var client = _factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, $"{TestConstants.RootAuthBasePath}/token");
        request.Content = JsonContent.Create(new
        {
            email = TestConstants.RootAdminEmail,
            password = TestConstants.DefaultPassword
        });

        using var response = await client.SendAsync(request);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var cookie = response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.FirstOrDefault(v => v.StartsWith("refresh_token=", StringComparison.Ordinal))
            : null;

        cookie.ShouldNotBeNull();
        cookie.ShouldContain("httponly", Case.Insensitive);
        cookie.ShouldContain("secure", Case.Insensitive);
        cookie.ShouldContain("samesite=strict", Case.Insensitive);
        cookie.ShouldContain($"path=/api/v1/tenants/{TestConstants.RootTenantId}/auth/refresh", Case.Insensitive);
    }

    [Fact]
    public async Task Refresh_Should_ReadTheTokenFromTheCookie_When_TheBodyOmitsIt()
    {
        var token = await _auth.GetRootAdminTokenAsync();

        using var client = _factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, $"{TestConstants.RootAuthBasePath}/refresh");
        request.Headers.Add("Cookie", $"refresh_token={token.RefreshToken}");
        request.Content = JsonContent.Create(new { token = token.AccessToken });

        using var response = await client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private static string? SidOf(string accessToken)
    {
        return new JwtSecurityTokenHandler()
            .ReadJwtToken(accessToken)
            .Claims
            .FirstOrDefault(c => c.Type == JwtRegisteredClaimNames.Sid)?.Value;
    }

    private async Task<HttpResponseMessage> RefreshAsync(string tenant, string? accessToken, string refreshToken)
    {
        using var client = _factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, $"{TestConstants.AuthBasePath(tenant)}/refresh");
        request.Content = JsonContent.Create(new { token = accessToken, refreshToken });
        return await client.SendAsync(request);
    }

    private static async Task CreateTenantAsync(HttpClient rootClient, string tenantId, string adminEmail)
    {
        var response = await rootClient.PostAsJsonAsync(TestConstants.TenantsBasePath, new
        {
            id = tenantId,
            name = $"Tenant {tenantId}",
            adminEmail,
            adminPassword = TestConstants.DefaultPassword,
            issuer = $"{tenantId}.issuer"
        });
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.ShouldBe(HttpStatusCode.Created, $"Create tenant failed: {body}");
    }

    private static async Task WaitForProvisioningAsync(HttpClient client, string tenantId, int maxRetries = 60)
    {
        for (int i = 0; i < maxRetries; i++)
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

        throw new TimeoutException($"Tenant {tenantId} provisioning did not complete within {maxRetries} seconds.");
    }
}
