using Boilerplate.BuildingBlocks.Shared.Multitenancy;
using Boilerplate.Modules.Identity.Data;
using Finbuckle.MultiTenant;
using Finbuckle.MultiTenant.Abstractions;
using Integration.Tests.Infrastructure;

namespace Integration.Tests.Tests.Sessions;

/// <summary>
/// Deactivating or deleting a user ends every one of their sessions (ASVS V7.4.2). The status
/// change, the revocation of each session row and the new security stamp are written in one save, and
/// with the per-request <c>sid</c> check the user's live access token stops working on its next
/// request rather than when it expires. Reactivation restores nothing: the user signs in again.
/// </summary>
[Collection(AppCollectionDefinition.Name)]
public sealed class DeactivatedUserSessionTests
{
    private const string MySessionsPath = $"{TestConstants.IdentityBasePath}/sessions/me";

    private readonly AppWebApplicationFactory _factory;
    private readonly AuthHelper _auth;

    public DeactivatedUserSessionTests(AppWebApplicationFactory factory)
    {
        _factory = factory;
        _auth = new AuthHelper(factory);
    }

    [Fact]
    public async Task DeactivatingAUser_Should_EndEveryOneOfTheirSessions()
    {
        // Arrange — the user is signed in on two devices, and both work.
        using var adminClient = await _auth.CreateRootAdminClientAsync();
        var user = await IdentityUserSeeder.CreateLoginableUserAsync(_factory, adminClient, "deactivated-sessions");
        var deviceA = await _auth.GetTokenAsync(user.Email, user.Password);
        var deviceB = await _auth.GetTokenAsync(user.Email, user.Password);
        using var clientA = BearerClient(deviceA.AccessToken);
        (await clientA.GetAsync(MySessionsPath)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var stampBefore = await SecurityStampOfAsync(user.UserId);

        // Act
        using var deactivate = await adminClient.PatchAsJsonAsync(
            $"{TestConstants.IdentityBasePath}/users/{user.UserId}",
            new { activateUser = false, userId = user.UserId });
        deactivate.StatusCode.ShouldBe(HttpStatusCode.NoContent, await deactivate.Content.ReadAsStringAsync());

        // Assert
        await AssertSignedOutEverywhereAsync(user.UserId, deviceA, deviceB, stampBefore);
        (await adminClient.GetAsync(MySessionsPath)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task DeletingAUser_Should_EndEveryOneOfTheirSessions()
    {
        // Arrange
        using var adminClient = await _auth.CreateRootAdminClientAsync();
        var user = await IdentityUserSeeder.CreateLoginableUserAsync(_factory, adminClient, "deleted-sessions");
        var deviceA = await _auth.GetTokenAsync(user.Email, user.Password);
        var deviceB = await _auth.GetTokenAsync(user.Email, user.Password);
        using var clientA = BearerClient(deviceA.AccessToken);
        (await clientA.GetAsync(MySessionsPath)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var stampBefore = await SecurityStampOfAsync(user.UserId);

        // Act
        using var delete = await adminClient.DeleteAsync($"{TestConstants.IdentityBasePath}/users/{user.UserId}");
        delete.StatusCode.ShouldBe(HttpStatusCode.NoContent, await delete.Content.ReadAsStringAsync());

        // Assert
        await AssertSignedOutEverywhereAsync(user.UserId, deviceA, deviceB, stampBefore);
    }

    [Fact]
    public async Task ReactivatingAUser_Should_RestoreNoSession_And_LeaveNewSignInsWorking()
    {
        // Arrange — a user whose sessions were ended by deactivation.
        using var adminClient = await _auth.CreateRootAdminClientAsync();
        var user = await IdentityUserSeeder.CreateLoginableUserAsync(_factory, adminClient, "reactivated-sessions");
        var oldDevice = await _auth.GetTokenAsync(user.Email, user.Password);
        using var deactivate = await adminClient.PatchAsJsonAsync(
            $"{TestConstants.IdentityBasePath}/users/{user.UserId}",
            new { activateUser = false, userId = user.UserId });
        deactivate.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // Act
        using var activate = await adminClient.PatchAsJsonAsync(
            $"{TestConstants.IdentityBasePath}/users/{user.UserId}",
            new { activateUser = true, userId = user.UserId });
        activate.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // Assert — the old token stays dead; a fresh sign-in works.
        using var oldClient = BearerClient(oldDevice.AccessToken);
        (await oldClient.GetAsync(MySessionsPath)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await RefreshAsync(oldDevice)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        var fresh = await _auth.GetTokenAsync(user.Email, user.Password);
        using var freshClient = BearerClient(fresh.AccessToken);
        (await freshClient.GetAsync(MySessionsPath)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private async Task AssertSignedOutEverywhereAsync(
        string userId,
        TokenResult deviceA,
        TokenResult deviceB,
        string? stampBefore)
    {
        // The next API call with either device's unexpired access token is refused.
        using var clientA = BearerClient(deviceA.AccessToken);
        using var clientB = BearerClient(deviceB.AccessToken);
        (await clientA.GetAsync(MySessionsPath)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await clientB.GetAsync(MySessionsPath)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // Neither refresh token can be spent.
        (await RefreshAsync(deviceA)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await RefreshAsync(deviceB)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // Every session row is revoked, and the security stamp moved.
        var sessions = await SessionRowsOfAsync(userId);
        sessions.Count.ShouldBeGreaterThanOrEqualTo(2);
        sessions.ShouldAllBe(s => s.IsRevoked);
        (await SecurityStampOfAsync(userId)).ShouldNotBe(stampBefore);
    }

    private async Task<HttpResponseMessage> RefreshAsync(TokenResult token)
    {
        using var client = _factory.CreateClient();
        return await client.PostAsJsonAsync(
            $"{TestConstants.RootAuthBasePath}/refresh",
            new { token = token.AccessToken, refreshToken = token.RefreshToken });
    }

    private HttpClient BearerClient(string accessToken)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return client;
    }

    private async Task<List<(bool IsRevoked, string? RevokedReason)>> SessionRowsOfAsync(string userId)
    {
        using var scope = _factory.Services.CreateScope();
        var tenant = await scope.ServiceProvider
            .GetRequiredService<IMultiTenantStore<AppTenantInfo>>()
            .GetAsync(TestConstants.RootTenantId);
        // Inline: Finbuckle's context is an AsyncLocal that does not flow out of an awaited helper.
        scope.ServiceProvider.GetRequiredService<IMultiTenantContextSetter>()
            .MultiTenantContext = new MultiTenantContext<AppTenantInfo>(tenant);

        var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        var rows = await db.UserSessions.AsNoTracking()
            .Where(s => s.UserId == userId)
            .Select(s => new { s.IsRevoked, s.RevokedReason })
            .ToListAsync();
        return rows.Select(r => (r.IsRevoked, r.RevokedReason)).ToList();
    }

    private async Task<string?> SecurityStampOfAsync(string userId)
    {
        using var scope = _factory.Services.CreateScope();
        var tenant = await scope.ServiceProvider
            .GetRequiredService<IMultiTenantStore<AppTenantInfo>>()
            .GetAsync(TestConstants.RootTenantId);
        scope.ServiceProvider.GetRequiredService<IMultiTenantContextSetter>()
            .MultiTenantContext = new MultiTenantContext<AppTenantInfo>(tenant);

        var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        return await db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => u.SecurityStamp)
            .SingleAsync();
    }
}
