using Boilerplate.Modules.Identity.Contracts.DTOs;
using Integration.Tests.Infrastructure;
using Integration.Tests.Tests.Sessions;

namespace Integration.Tests.Tests.Authentication;

/// <summary>
/// Logout has to end the *server's* session, not just the client's memory of it. The refresh token
/// lives in an HttpOnly cookie the SPA cannot delete, and the refresh endpoint accepts that cookie
/// on its own — so clearing localStorage alone would leave a full token pair recoverable by anyone
/// at the browser. These tests pin the three things that close that hole: the session row is
/// revoked, the cookie is cleared, and a failed rotation clears it too.
/// </summary>
[Collection(AppCollectionDefinition.Name)]
public sealed class LogoutTests
{
    private readonly AppWebApplicationFactory _factory;
    private readonly AuthHelper _auth;

    public LogoutTests(AppWebApplicationFactory factory)
    {
        _factory = factory;
        _auth = new AuthHelper(factory);
    }

    [Fact]
    public async Task Logout_Should_RevokeTheSession_And_ClearTheCookie_When_AccessTokenIsPresented()
    {
        // Arrange
        using var adminClient = await _auth.CreateRootAdminClientAsync();
        var user = await IdentityUserSeeder.CreateLoginableUserAsync(_factory, adminClient, "logout-bearer");
        var token = await _auth.GetTokenAsync(user.Email, user.Password);

        // Act
        using var response = await LogoutAsync(TestConstants.RootTenantId, accessToken: token.AccessToken);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        ClearsRefreshCookie(response, TestConstants.RootTenantId).ShouldBeTrue();

        // The refresh token is dead in the body...
        using var bodyRefresh = await RefreshAsync(TestConstants.RootTenantId, token.AccessToken, token.RefreshToken);
        bodyRefresh.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // ...and dead in the cookie, which is the path a browser would take.
        using var cookieRefresh = await RefreshWithCookieAsync(TestConstants.RootTenantId, token.RefreshToken);
        cookieRefresh.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // And the row itself is revoked, so it no longer shows as an active session.
        (await ActiveSessionCountAsync(token.AccessToken)).ShouldBe(0);
    }

    [Fact]
    public async Task Logout_Should_RevokeTheSession_When_OnlyTheRefreshCookieIsPresented()
    {
        // Arrange — the browser case the hole lived in: the access token has been dropped from
        // localStorage but the HttpOnly cookie is still riding along.
        using var adminClient = await _auth.CreateRootAdminClientAsync();
        var user = await IdentityUserSeeder.CreateLoginableUserAsync(_factory, adminClient, "logout-cookie");
        var token = await _auth.GetTokenAsync(user.Email, user.Password);

        // Act
        using var response = await LogoutAsync(
            TestConstants.RootTenantId, accessToken: null, refreshCookie: token.RefreshToken);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        ClearsRefreshCookie(response, TestConstants.RootTenantId).ShouldBeTrue();

        using var afterLogout = await RefreshAsync(TestConstants.RootTenantId, token.AccessToken, token.RefreshToken);
        afterLogout.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Logout_Should_RevokeTheSession_When_TheRefreshTokenIsInTheBody()
    {
        // Arrange — native clients hold the token in the body, not a cookie.
        using var adminClient = await _auth.CreateRootAdminClientAsync();
        var user = await IdentityUserSeeder.CreateLoginableUserAsync(_factory, adminClient, "logout-body");
        var token = await _auth.GetTokenAsync(user.Email, user.Password);

        // Act
        using var response = await LogoutAsync(
            TestConstants.RootTenantId, accessToken: null, body: new { refreshToken = token.RefreshToken });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        using var afterLogout = await RefreshAsync(TestConstants.RootTenantId, token.AccessToken, token.RefreshToken);
        afterLogout.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Logout_Should_LeaveOtherDevicesSignedIn()
    {
        // Arrange
        using var adminClient = await _auth.CreateRootAdminClientAsync();
        var user = await IdentityUserSeeder.CreateLoginableUserAsync(_factory, adminClient, "logout-two-dev");
        var deviceA = await _auth.GetTokenAsync(user.Email, user.Password);
        var deviceB = await _auth.GetTokenAsync(user.Email, user.Password);

        // Act — device A signs out.
        using var response = await LogoutAsync(TestConstants.RootTenantId, accessToken: deviceA.AccessToken);
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // Assert — logout ends one session, not the account.
        using var a = await RefreshAsync(TestConstants.RootTenantId, deviceA.AccessToken, deviceA.RefreshToken);
        a.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        using var b = await RefreshAsync(TestConstants.RootTenantId, deviceB.AccessToken, deviceB.RefreshToken);
        b.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("root.not-a-real-token")]
    public async Task Logout_Should_Return204_And_ClearTheCookie_When_NothingValidIsPresented(string? refreshCookie)
    {
        // A logout that answered differently for a live token than for a dead one would be an
        // oracle for guessing tokens. It always succeeds and always clears the cookie.
        using var response = await LogoutAsync(
            TestConstants.RootTenantId, accessToken: null, refreshCookie: refreshCookie);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        ClearsRefreshCookie(response, TestConstants.RootTenantId).ShouldBeTrue();
    }

    [Fact]
    public async Task FailedRotation_Should_ClearTheRefreshCookie()
    {
        // A cookie the server has just refused is dead weight: leaving it in place means every
        // later request carries a credential that can only ever fail.
        using var client = _factory.CreateClient();
        var request = new HttpRequestMessage(
            HttpMethod.Post, $"{TestConstants.AuthBasePath(TestConstants.RootTenantId)}/refresh");
        request.Headers.Add("Cookie", $"refresh_token={TestConstants.RootTenantId}.dead-token");
        request.Content = JsonContent.Create(new { });

        using var response = await client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        ClearsRefreshCookie(response, TestConstants.RootTenantId).ShouldBeTrue();
    }

    [Fact]
    public async Task SuccessfulRotation_Should_ReplaceTheCookie_NotClearIt()
    {
        var token = await _auth.GetRootAdminTokenAsync();

        using var response = await RefreshAsync(TestConstants.RootTenantId, token.AccessToken, token.RefreshToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        ClearsRefreshCookie(response, TestConstants.RootTenantId).ShouldBeFalse();
        RefreshCookieHeader(response).ShouldNotBeNull();
    }

    private async Task<int> ActiveSessionCountAsync(string accessToken)
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var response = await client.GetAsync($"{TestConstants.IdentityBasePath}/sessions/me");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var sessions = await response.Content.ReadFromJsonAsync<List<UserSessionDto>>();
        return sessions?.Count ?? 0;
    }

    private static string? RefreshCookieHeader(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.FirstOrDefault(v => v.StartsWith("refresh_token=", StringComparison.Ordinal))
            : null;

    /// <summary>
    /// A deletion is a Set-Cookie with an empty value and an expiry in the past, carrying the same
    /// Path/HttpOnly/Secure/SameSite as the cookie it replaces — a browser ignores it otherwise.
    /// </summary>
    private static bool ClearsRefreshCookie(HttpResponseMessage response, string tenant)
    {
        var cookie = RefreshCookieHeader(response);
        if (cookie is null || !cookie.StartsWith("refresh_token=;", StringComparison.Ordinal))
        {
            return false;
        }

        cookie.ShouldContain("expires=Thu, 01 Jan 1970", Case.Insensitive);
        cookie.ShouldContain($"path=/api/v1/tenants/{tenant}/auth/refresh", Case.Insensitive);
        cookie.ShouldContain("httponly", Case.Insensitive);
        cookie.ShouldContain("secure", Case.Insensitive);
        cookie.ShouldContain("samesite=strict", Case.Insensitive);
        return true;
    }

    private async Task<HttpResponseMessage> LogoutAsync(
        string tenant, string? accessToken, string? refreshCookie = null, object? body = null)
    {
        using var client = _factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, $"{TestConstants.AuthBasePath(tenant)}/logout");

        if (accessToken is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        if (refreshCookie is not null)
        {
            request.Headers.Add("Cookie", $"refresh_token={refreshCookie}");
        }

        request.Content = JsonContent.Create(body ?? new { });
        return await client.SendAsync(request);
    }

    private async Task<HttpResponseMessage> RefreshAsync(string tenant, string? accessToken, string refreshToken)
    {
        using var client = _factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, $"{TestConstants.AuthBasePath(tenant)}/refresh");
        request.Content = JsonContent.Create(new { token = accessToken, refreshToken });
        return await client.SendAsync(request);
    }

    private async Task<HttpResponseMessage> RefreshWithCookieAsync(string tenant, string refreshToken)
    {
        using var client = _factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, $"{TestConstants.AuthBasePath(tenant)}/refresh");
        request.Headers.Add("Cookie", $"refresh_token={refreshToken}");
        request.Content = JsonContent.Create(new { });
        return await client.SendAsync(request);
    }
}
