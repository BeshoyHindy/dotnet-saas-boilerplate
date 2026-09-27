using Integration.Tests.Infrastructure;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Text;

namespace Integration.Tests.Tests.Sessions;

/// <summary>
/// Revoking a session takes effect on the next request, not when the access token expires (#118).
/// Every access token minted at login or refresh names its session in <c>sid</c>, and authentication
/// refuses the token with 401 once that session is revoked, expired or gone. A token that names no
/// session at all is refused too — nothing could ever revoke it.
/// </summary>
[Collection(AppCollectionDefinition.Name)]
public sealed class RevokedSessionAccessTests
{
    private const string MySessionsPath = $"{TestConstants.IdentityBasePath}/sessions/me";

    private readonly AppWebApplicationFactory _factory;
    private readonly AuthHelper _auth;

    public RevokedSessionAccessTests(AppWebApplicationFactory factory)
    {
        _factory = factory;
        _auth = new AuthHelper(factory);
    }

    #region Revocation bites on the next request

    [Fact]
    public async Task AccessToken_Should_Return401_On_TheNextRequest_When_ItsSessionIsRevoked()
    {
        // Arrange — sign in, and prove the token works before the revoke.
        using var adminClient = await _auth.CreateRootAdminClientAsync();
        var user = await IdentityUserSeeder.CreateLoginableUserAsync(_factory, adminClient, "revoked-next");
        var token = await _auth.GetTokenAsync(user.Email, user.Password);
        using var client = BearerClient(token.AccessToken);
        (await client.GetAsync(MySessionsPath)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // Act — revoke that very session, with the same token.
        using var revoke = await client.DeleteAsync(
            $"{TestConstants.IdentityBasePath}/sessions/{SidOf(token.AccessToken)}");
        revoke.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // Assert — the access token has not expired, but its session has ended.
        using var next = await client.GetAsync(MySessionsPath);
        next.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task AccessToken_Should_Return401_When_AnAdminRevokesAllTheUsersSessions()
    {
        // Arrange — the user holds a working token; the admin's own session is unrelated.
        using var adminClient = await _auth.CreateRootAdminClientAsync();
        var user = await IdentityUserSeeder.CreateLoginableUserAsync(_factory, adminClient, "revoked-admin-all");
        var token = await _auth.GetTokenAsync(user.Email, user.Password);
        using var client = BearerClient(token.AccessToken);
        (await client.GetAsync(MySessionsPath)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // Act
        using var revoke = await adminClient.PostAsync(
            $"{TestConstants.IdentityBasePath}/users/{user.UserId}/sessions/revoke-all", content: null);
        revoke.StatusCode.ShouldBe(HttpStatusCode.OK);

        // Assert — the user is out; the admin who did it is not.
        (await client.GetAsync(MySessionsPath)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await adminClient.GetAsync(MySessionsPath)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task AccessToken_Should_Return401_When_ItsSessionWasEndedByLogout()
    {
        // Arrange
        using var adminClient = await _auth.CreateRootAdminClientAsync();
        var user = await IdentityUserSeeder.CreateLoginableUserAsync(_factory, adminClient, "revoked-logout");
        var token = await _auth.GetTokenAsync(user.Email, user.Password);
        using var client = BearerClient(token.AccessToken);

        // Act
        using var logout = await client.PostAsJsonAsync(
            $"{TestConstants.AuthBasePath(TestConstants.RootTenantId)}/logout", new { });
        logout.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // Assert
        (await client.GetAsync(MySessionsPath)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task RevokingOneSession_Should_LeaveTheUsersOtherSessionsWorking()
    {
        // Arrange — two devices.
        using var adminClient = await _auth.CreateRootAdminClientAsync();
        var user = await IdentityUserSeeder.CreateLoginableUserAsync(_factory, adminClient, "revoked-one-of-two");
        var deviceA = await _auth.GetTokenAsync(user.Email, user.Password);
        var deviceB = await _auth.GetTokenAsync(user.Email, user.Password);
        using var clientA = BearerClient(deviceA.AccessToken);
        using var clientB = BearerClient(deviceB.AccessToken);

        // Act — device A's session is revoked.
        using var revoke = await clientA.DeleteAsync(
            $"{TestConstants.IdentityBasePath}/sessions/{SidOf(deviceA.AccessToken)}");
        revoke.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // Assert
        (await clientA.GetAsync(MySessionsPath)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await clientB.GetAsync(MySessionsPath)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    #endregion

    #region Tokens that name no live session

    [Fact]
    public async Task AccessToken_Should_Return401_When_ItCarriesNoSid()
    {
        // Arrange — a real user's real token, re-signed with the host's key minus its sid claim. Apart
        // from the missing sid it is the token login issued, so only the sid rule can refuse it.
        using var adminClient = await _auth.CreateRootAdminClientAsync();
        var user = await IdentityUserSeeder.CreateLoginableUserAsync(_factory, adminClient, "sid-less");
        var token = await _auth.GetTokenAsync(user.Email, user.Password);
        using var client = BearerClient(Resign(token.AccessToken, sid: null));

        // Act
        using var response = await client.GetAsync(MySessionsPath);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task AccessToken_Should_Return401_When_ItsSidNamesNoSession()
    {
        using var adminClient = await _auth.CreateRootAdminClientAsync();
        var user = await IdentityUserSeeder.CreateLoginableUserAsync(_factory, adminClient, "sid-unknown");
        var token = await _auth.GetTokenAsync(user.Email, user.Password);
        using var client = BearerClient(Resign(token.AccessToken, sid: Guid.NewGuid().ToString()));

        using var response = await client.GetAsync(MySessionsPath);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ResignedToken_Should_StillWork_When_ItKeepsItsLiveSid()
    {
        // Positive control for the two tests above: the re-signing itself is not what they refuse.
        using var adminClient = await _auth.CreateRootAdminClientAsync();
        var user = await IdentityUserSeeder.CreateLoginableUserAsync(_factory, adminClient, "sid-control");
        var token = await _auth.GetTokenAsync(user.Email, user.Password);
        using var client = BearerClient(Resign(token.AccessToken, sid: SidOf(token.AccessToken)));

        using var response = await client.GetAsync(MySessionsPath);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    #endregion

    private HttpClient BearerClient(string accessToken)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return client;
    }

    private static string SidOf(string accessToken) =>
        new JwtSecurityTokenHandler()
            .ReadJwtToken(accessToken)
            .Claims
            .First(c => c.Type == JwtRegisteredClaimNames.Sid)
            .Value;

    /// <summary>
    /// Copies every claim of <paramref name="accessToken"/> into a fresh token signed with the test
    /// host's key, with <c>sid</c> replaced by <paramref name="sid"/> — or dropped when it is null.
    /// </summary>
    private static string Resign(string accessToken, string? sid)
    {
        string[] envelope =
        [
            JwtRegisteredClaimNames.Sid,
            JwtRegisteredClaimNames.Iss,
            JwtRegisteredClaimNames.Aud,
            JwtRegisteredClaimNames.Exp,
            JwtRegisteredClaimNames.Nbf,
            JwtRegisteredClaimNames.Iat,
        ];

        var original = new JwtSecurityTokenHandler().ReadJwtToken(accessToken);
        var claims = original.Claims.Where(c => !envelope.Contains(c.Type)).ToList();
        if (sid is not null)
        {
            claims.Add(new System.Security.Claims.Claim(JwtRegisteredClaimNames.Sid, sid));
        }

        var key = new SymmetricSecurityKey(Encoding.ASCII.GetBytes(TestConstants.JwtSigningKey));
        var token = new JwtSecurityToken(
            issuer: TestConstants.JwtIssuer,
            audience: TestConstants.JwtAudience,
            claims: claims,
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: DateTime.UtcNow.AddMinutes(15),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
