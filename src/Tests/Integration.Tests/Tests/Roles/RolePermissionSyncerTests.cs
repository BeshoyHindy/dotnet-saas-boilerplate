using Finbuckle.MultiTenant;
using Finbuckle.MultiTenant.Abstractions;
using Boilerplate.BuildingBlocks.Shared.Constants;
using Boilerplate.BuildingBlocks.Shared.Multitenancy;
using Boilerplate.Modules.Files.Contracts.Authorization;
using Boilerplate.Modules.Identity.Authorization;
using Boilerplate.Modules.Identity.Data;
using Boilerplate.Modules.Identity.Domain;
using Integration.Tests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Integration.Tests.Tests.Roles;

/// <summary>
/// Direct service-level test for <see cref="RolePermissionSyncer"/>. Verifies the
/// "permissions added in a release after tenant was already provisioned" path —
/// which the per-tenant <c>IDbInitializer.SeedAsync</c> flow does not run again
/// for already-provisioned tenants in production. This is the path that produced
/// 401s in dev when a new module was added.
/// </summary>
[Collection(AppCollectionDefinition.Name)]
public sealed class RolePermissionSyncerTests
{
    private readonly AppWebApplicationFactory _factory;

    public RolePermissionSyncerTests(AppWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task SyncAsync_Should_Restore_Missing_Permission_Claims_For_Admin_Role()
    {
        var rootTenant = await GetRootTenantAsync();

        var filesPermissions = FilesPermissions.All.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

        // 1. Wipe Admin's Files.* claims directly. Simulates "perms registered in a
        //    later release that never made it into this tenant's RoleClaims table."
        await WipeClaimsAsync(rootTenant, "Admin", filesPermissions);

        var afterWipe = await GetClaimsAsync(rootTenant, "Admin");
        afterWipe.Intersect(filesPermissions).ShouldBeEmpty(
            "Pre-condition: Admin must not have any Files claims after the wipe.");

        // 2. Run the syncer through the production scope/tenant pattern.
        using (var scope = _factory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<IMultiTenantContextSetter>()
                .MultiTenantContext = new MultiTenantContext<AppTenantInfo>(rootTenant);

            var syncer = scope.ServiceProvider.GetRequiredService<RolePermissionSyncer>();
            await syncer.SyncAsync(CancellationToken.None);
        }

        // 3. All Files perms should be back on the Admin role.
        var afterSync = await GetClaimsAsync(rootTenant, "Admin");
        var missing = filesPermissions.Where(p => !afterSync.Contains(p)).ToList();
        missing.ShouldBeEmpty(
            $"Syncer failed to restore {missing.Count} files permission(s): " +
            $"[{string.Join(", ", missing)}]");
    }

    [Fact]
    public async Task SyncAsync_Should_Be_Idempotent_When_Claims_Already_Exist()
    {
        var rootTenant = await GetRootTenantAsync();
        var before = await GetClaimsAsync(rootTenant, "Admin");

        // Running the syncer twice in a row must not duplicate claims or throw.
        using (var scope = _factory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<IMultiTenantContextSetter>()
                .MultiTenantContext = new MultiTenantContext<AppTenantInfo>(rootTenant);

            var syncer = scope.ServiceProvider.GetRequiredService<RolePermissionSyncer>();
            await syncer.SyncAsync(CancellationToken.None);
            await syncer.SyncAsync(CancellationToken.None);
        }

        var after = await GetClaimsAsync(rootTenant, "Admin");
        after.Count.ShouldBe(before.Count, "Syncer must not duplicate existing permission claims.");
    }

    // ─── helpers ─────────────────────────────────────────────────────

    private async Task<AppTenantInfo> GetRootTenantAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IMultiTenantStore<AppTenantInfo>>();
        var tenant = await store.GetAsync(MultitenancyConstants.Root.Id);
        tenant.ShouldNotBeNull("Root tenant must be provisioned before this test runs.");
        return tenant;
    }

    private async Task WipeClaimsAsync(AppTenantInfo tenant, string roleName, IReadOnlyCollection<string> claimValues)
    {
        using var scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IMultiTenantContextSetter>()
            .MultiTenantContext = new MultiTenantContext<AppTenantInfo>(tenant);

        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<AppRole>>();
        var role = await roleManager.Roles.SingleAsync(r => r.Name == roleName);

        var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        var values = claimValues.ToHashSet(StringComparer.Ordinal);

        // ExecuteDeleteAsync would be cleaner but the in-memory enumeration is
        // negligible for a handful of claims and avoids parameter-list edge cases.
        var toRemove = await db.RoleClaims
            .Where(rc => rc.RoleId == role.Id && rc.ClaimType == ClaimConstants.Permission)
            .ToListAsync();

        db.RoleClaims.RemoveRange(toRemove.Where(rc => rc.ClaimValue is not null && values.Contains(rc.ClaimValue)));
        await db.SaveChangesAsync();
    }

    private async Task<HashSet<string>> GetClaimsAsync(AppTenantInfo tenant, string roleName)
    {
        using var scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IMultiTenantContextSetter>()
            .MultiTenantContext = new MultiTenantContext<AppTenantInfo>(tenant);

        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<AppRole>>();
        var role = await roleManager.Roles.SingleAsync(r => r.Name == roleName);

        var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        var claims = await db.RoleClaims
            .Where(rc => rc.RoleId == role.Id && rc.ClaimType == ClaimConstants.Permission)
            .Select(rc => rc.ClaimValue!)
            .ToListAsync();

        return claims.ToHashSet(StringComparer.Ordinal);
    }
}
