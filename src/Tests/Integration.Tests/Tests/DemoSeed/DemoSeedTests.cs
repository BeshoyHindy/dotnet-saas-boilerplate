extern alias migrator;

using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Boilerplate.BuildingBlocks.Shared.Constants;
using Boilerplate.BuildingBlocks.Shared.Multitenancy;
using Boilerplate.Modules.Identity.Contracts.Authorization;
using Boilerplate.Modules.Identity.Data;
using Boilerplate.Modules.Identity.Domain;
using Finbuckle.MultiTenant.Abstractions;
using Integration.Tests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using migrator::Boilerplate.DbMigrator.DemoSeed;

namespace Integration.Tests.Tests.DemoSeed;

/// <summary>
/// End-to-end coverage for the migrator's <c>apply --demo</c> seeding: the demo accounts are only
/// worth shipping if they can actually sign in, so this drives the real <c>DemoSeeder</c> against
/// the real host and then authenticates through the real login endpoint.
///
/// Runs inside the shared app collection rather than standing up its own factory: two
/// <c>WebApplicationFactory</c> instances built in parallel race on the static module loader and
/// on Hangfire's global storage. The cost is that the demo tenants live in the suite's shared
/// database — which is no different from what every other class here does when it provisions a
/// tenant, and nothing in the suite asserts an exact tenant count.
/// </summary>
[Collection(AppCollectionDefinition.Name)]
public sealed class DemoSeedTests : IAsyncLifetime
{
    private const string AcmeId = "acme";
    private const string GlobexId = "globex";
    private const string AcmeAdminEmail = "admin@acme.com";
    private const string GlobexAdminEmail = "admin@globex.com";
    private const string AcmeBasicEmail = "alice@acme.com";
    private const string AcmeManagerEmail = "manager@acme.com";

    private const string PermissionsPath = TestConstants.IdentityBasePath + "/permissions";

    private readonly AppWebApplicationFactory _factory;
    private readonly AuthHelper _auth;

    public DemoSeedTests(AppWebApplicationFactory factory)
    {
        _factory = factory;
        _auth = new AuthHelper(factory);
    }

    public Task InitializeAsync() => RunDemoSeederAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>Runs the seeder exactly as <c>Program.cs</c> does when <c>--demo</c> is passed.</summary>
    private Task RunDemoSeederAsync()
    {
        var seeder = new DemoSeeder(
            _factory.Services,
            _factory.Services.GetRequiredService<ILogger<DemoSeeder>>());
        return seeder.RunAsync(CancellationToken.None);
    }

    // ─── the tenants ─────────────────────────────────────────────────────

    [Fact]
    public async Task DemoSeeding_Should_CreateBothDemoTenants_Active()
    {
        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IMultiTenantStore<AppTenantInfo>>();

        foreach (var id in new[] { AcmeId, GlobexId })
        {
            var tenant = await store.GetAsync(id);
            tenant.ShouldNotBeNull($"demo tenant '{id}' should exist after demo seeding");
            tenant!.IsActive.ShouldBeTrue();
            tenant.ValidUpto.ShouldBeGreaterThan(DateTime.UtcNow);
        }
    }

    /// <summary>
    /// The console's Provisioning panel reads this; a demo tenant with no provisioning history
    /// would 404 there and look broken.
    /// </summary>
    [Fact]
    public async Task DemoSeeding_Should_RecordCompletedProvisioning()
    {
        using var rootClient = await _auth.CreateRootAdminClientAsync();

        var response = await rootClient.GetAsync($"{TestConstants.TenantsBasePath}/{AcmeId}/provisioning");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).ShouldContain("Completed");
    }

    // ─── the accounts ────────────────────────────────────────────────────

    [Fact]
    public async Task DemoTenantAdmin_Should_SignIn_WithTheConfiguredDemoPassword()
    {
        var token = await _auth.GetTokenAsync(AcmeAdminEmail, TestConstants.DemoPassword, AcmeId);

        token.AccessToken.ShouldNotBeNullOrWhiteSpace();
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token.AccessToken);
        jwt.Claims.First(c => c.Type == ClaimConstants.Tenant).Value.ShouldBe(AcmeId);
        jwt.Claims.Where(c => c.Type == ClaimTypes.Role).Select(c => c.Value)
            .ShouldContain(RoleConstants.Admin);
    }

    [Fact]
    public async Task EveryDemoAccount_Should_SignIn_WithTheSameDemoPassword()
    {
        // The shared password is the whole point of the demo set: one value on the login panel.
        foreach (var (email, tenant) in new[]
                 {
                     (AcmeAdminEmail, AcmeId),
                     (AcmeManagerEmail, AcmeId),
                     ("support@acme.com", AcmeId),
                     (AcmeBasicEmail, AcmeId),
                     ("bob@acme.com", AcmeId),
                     (GlobexAdminEmail, GlobexId),
                     ("dave@globex.com", GlobexId),
                 })
        {
            var token = await _auth.GetTokenAsync(email, TestConstants.DemoPassword, tenant);
            token.AccessToken.ShouldNotBeNullOrWhiteSpace($"{email} should be able to sign in");
        }
    }

    [Fact]
    public async Task BasicDemoUser_Should_Hold_ExactlyTheBasicPermissionSet()
    {
        using var client = await _auth.CreateAuthenticatedClientAsync(
            AcmeBasicEmail, TestConstants.DemoPassword, AcmeId);

        var permissions = await client.GetFromJsonAsync<List<string>>(PermissionsPath);

        var expected = _factory.Services.GetRequiredService<IPermissionRegistry>()
            .Basic.Select(p => p.Name);
        permissions.ShouldNotBeNull();
        permissions!.OrderBy(p => p, StringComparer.Ordinal)
            .ShouldBe(expected.OrderBy(p => p, StringComparer.Ordinal));
    }

    /// <summary>
    /// The custom role is the reason it is worth seeding one: a Manager holds more than Basic and
    /// less than Admin, which is what makes the permission editor interesting to look at.
    /// </summary>
    [Fact]
    public async Task ManagerDemoUser_Should_Hold_TheCustomRolePermissions()
    {
        using var client = await _auth.CreateAuthenticatedClientAsync(
            AcmeManagerEmail, TestConstants.DemoPassword, AcmeId);

        var permissions = await client.GetFromJsonAsync<List<string>>(PermissionsPath);

        permissions.ShouldNotBeNull();
        permissions!.ShouldContain(IdentityPermissions.Users.Update);
        permissions.ShouldContain(IdentityPermissions.Groups.ManageMembers);
        // Not an administrator: the role grants no user deletion and no role editing.
        permissions.ShouldNotContain(IdentityPermissions.Users.Delete);
        permissions.ShouldNotContain(IdentityPermissions.Roles.Create);
    }

    [Fact]
    public async Task DemoUser_Should_NotSignIn_UnderTheOtherDemoTenant()
    {
        using var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            $"{TestConstants.AuthBasePath(GlobexId)}/token",
            new { email = AcmeBasicEmail, password = TestConstants.DemoPassword });

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task DemoSeeding_Should_PutUsersInTheirGroups()
    {
        var members = await ReadGroupMemberEmailsAsync(AcmeId, "Engineering");

        members.ShouldContain(AcmeBasicEmail);
        members.ShouldContain("bob@acme.com");
        // "All Users" is the framework's default group; demo users join it like any registration.
        var allUsers = await ReadGroupMemberEmailsAsync(AcmeId, "All Users");
        allUsers.ShouldContain(AcmeBasicEmail);
    }

    // ─── idempotency ─────────────────────────────────────────────────────

    [Fact]
    public async Task SecondRun_Should_ChangeNothing()
    {
        var before = await SnapshotAsync();

        await RunDemoSeederAsync();

        var after = await SnapshotAsync();
        after.ShouldBe(before);

        // And the accounts still work — a re-run that silently rewrote a credential would pass a
        // pure row-count check.
        var token = await _auth.GetTokenAsync(AcmeAdminEmail, TestConstants.DemoPassword, AcmeId);
        token.AccessToken.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task SecondRun_Should_NotResetAPasswordSomeoneChanged()
    {
        const string ChangedPassword = "Rotated$Pass77";
        await SetPasswordAsync(GlobexId, "dave@globex.com", ChangedPassword);

        await RunDemoSeederAsync();

        // The developer's password still works…
        var token = await _auth.GetTokenAsync("dave@globex.com", ChangedPassword, GlobexId);
        token.AccessToken.ShouldNotBeNullOrWhiteSpace();

        // …and the seeder did not put the demo one back.
        using var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync(
            $"{TestConstants.AuthBasePath(GlobexId)}/token",
            new { email = "dave@globex.com", password = TestConstants.DemoPassword });
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // Leave the tenant as the other tests expect to find it.
        await SetPasswordAsync(GlobexId, "dave@globex.com", TestConstants.DemoPassword);
    }

    // ─── helpers ─────────────────────────────────────────────────────────

    /// <summary>Row counts a second run must not move, per demo tenant.</summary>
    private async Task<List<TenantCounts>> SnapshotAsync()
    {
        var scope = _factory.Services.GetRequiredService<ITenantScope>();

        var counts = new List<TenantCounts>();
        foreach (var tenantId in new[] { AcmeId, GlobexId })
        {
            counts.Add(await scope.RunAsync(tenantId, async (provider, ct) =>
            {
                var context = provider.GetRequiredService<IdentityDbContext>();
                return new TenantCounts(
                    Users: await context.Users.CountAsync(ct),
                    Roles: await context.Roles.CountAsync(ct),
                    RoleClaims: await context.RoleClaims.CountAsync(ct),
                    Groups: await context.Groups.CountAsync(ct),
                    Memberships: await context.UserGroups.CountAsync(ct));
            }));
        }

        return counts;
    }

    private async Task<List<string>> ReadGroupMemberEmailsAsync(string tenantId, string groupName)
    {
        var scope = _factory.Services.GetRequiredService<ITenantScope>();

        return await scope.RunAsync(tenantId, async (provider, ct) =>
        {
            var context = provider.GetRequiredService<IdentityDbContext>();
            var group = await context.Groups.FirstOrDefaultAsync(g => g.Name == groupName, ct);
            group.ShouldNotBeNull($"group '{groupName}' should exist in '{tenantId}'");

            return await context.UserGroups
                .Where(ug => ug.GroupId == group!.Id)
                .Join(context.Users, ug => ug.UserId, u => u.Id, (_, u) => u.Email!)
                .ToListAsync(ct);
        });
    }

    private async Task SetPasswordAsync(string tenantId, string email, string password)
    {
        var scope = _factory.Services.GetRequiredService<ITenantScope>();

        await scope.RunAsync(tenantId, async (provider, _) =>
        {
            var userManager = provider.GetRequiredService<UserManager<AppUser>>();
            var user = await userManager.FindByEmailAsync(email);
            user.ShouldNotBeNull();

            var token = await userManager.GeneratePasswordResetTokenAsync(user!);
            (await userManager.ResetPasswordAsync(user!, token, password)).Succeeded.ShouldBeTrue();
        });
    }

    private sealed record TenantCounts(int Users, int Roles, int RoleClaims, int Groups, int Memberships);
}
