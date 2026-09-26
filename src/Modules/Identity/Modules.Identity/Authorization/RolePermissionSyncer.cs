using Boilerplate.BuildingBlocks.Caching;
using Boilerplate.BuildingBlocks.Shared.Constants;
using Boilerplate.BuildingBlocks.Shared.Identity.Claims;
using Boilerplate.BuildingBlocks.Shared.Multitenancy;
using Boilerplate.Modules.Identity.Data;
using Boilerplate.Modules.Identity.Domain;
using Finbuckle.MultiTenant.Abstractions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging;

namespace Boilerplate.Modules.Identity.Authorization;

/// <summary>
/// Adds missing permission claims to, and removes stale ones from, the built-in roles
/// (<see cref="RoleConstants.Admin"/>, <see cref="RoleConstants.Basic"/>) for the current Finbuckle
/// tenant context, and prunes every other (tenant-created) role down to permissions that still exist
/// in <see cref="IPermissionRegistry.All"/>. Idempotent — a claim that already matches its target is
/// left untouched, so it can run on every startup safely.
/// </summary>
public sealed class RolePermissionSyncer(
    IdentityDbContext context,
    RoleManager<AppRole> roleManager,
    IMultiTenantContextAccessor<AppTenantInfo> tenantAccessor,
    HybridCache cache,
    TimeProvider timeProvider,
    IPermissionRegistry permissionRegistry,
    ILogger<RolePermissionSyncer> logger)
{
    public async Task SyncAsync(CancellationToken cancellationToken)
    {
        var tenantId = tenantAccessor.MultiTenantContext.TenantInfo?.Id;
        bool isRoot = tenantId == MultitenancyConstants.Root.Id;

        int changed = await SyncRoleAsync(RoleConstants.Basic, permissionRegistry.Basic, addMissing: true, cancellationToken).ConfigureAwait(false);

        // Admin gets all non-root permissions; the root tenant's Admin additionally gets Root permissions.
        var adminPermissions = isRoot
            ? permissionRegistry.Admin.Concat(permissionRegistry.Root).Distinct().ToList()
            : permissionRegistry.Admin.ToList();
        changed += await SyncRoleAsync(RoleConstants.Admin, adminPermissions, addMissing: true, cancellationToken).ConfigureAwait(false);

        // Every other role is one a tenant created for itself (RoleService.CreateOrUpdateRoleAsync).
        // Its permission set is the tenant's own choice, so we never add to it — we only drop claims
        // that no longer name a real permission, e.g. one whose module was removed from the registry.
        var customRoles = await roleManager.Roles
            .Where(r => r.Name != RoleConstants.Admin && r.Name != RoleConstants.Basic)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (var customRole in customRoles)
        {
            if (customRole.Name is null)
            {
                continue;
            }

            changed += await SyncRoleAsync(customRole.Name, permissionRegistry.All, addMissing: false, cancellationToken).ConfigureAwait(false);
        }

        // If we changed anything, drop the per-user permission cache so already-logged-in
        // sessions see the new perms on their next request rather than waiting for TTL.
        // The tag is scoped to the ambient tenant by the cache, so this evicts only the tenant whose
        // claims we just changed. That is the right blast radius: the syncer is invoked once per
        // tenant under ITenantScope.RunAsync (RolePermissionSyncHostedService), so every tenant whose
        // claims changed gets its own eviction, and a tenant that changed none keeps its warm entries.
        if (changed > 0)
        {
            await cache.RemoveByTagAsync(CacheKeys.Tags.Permissions, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Diffs <paramref name="roleName"/>'s permission claims against <paramref name="targetPermissions"/>:
    /// removes any claim whose value isn't in the target set, and — when <paramref name="addMissing"/> is
    /// true — adds every target permission the role doesn't already have. Returns the number of claims
    /// added plus removed.
    /// </summary>
    private async Task<int> SyncRoleAsync(string roleName, IReadOnlyList<AppPermission> targetPermissions, bool addMissing, CancellationToken cancellationToken)
    {
        var role = await roleManager.Roles
            .SingleOrDefaultAsync(r => r.Name == roleName, cancellationToken)
            .ConfigureAwait(false);
        if (role is null)
        {
            // Role not yet seeded — full IdentityDbInitializer.SeedAsync will create it the first time.
            return 0;
        }

        var existing = await context.RoleClaims
            .Where(rc => rc.RoleId == role.Id && rc.ClaimType == ClaimConstants.Permission)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var targetNames = targetPermissions.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

        var toRemove = existing
            .Where(rc => rc.ClaimValue is null || !targetNames.Contains(rc.ClaimValue))
            .ToList();

        var toAdd = new List<AppRoleClaim>();
        if (addMissing)
        {
            var existingSet = existing.Select(rc => rc.ClaimValue!).ToHashSet(StringComparer.Ordinal);
            toAdd = targetPermissions
                .Where(p => !existingSet.Contains(p.Name))
                .Select(p => new AppRoleClaim
                {
                    RoleId = role.Id,
                    ClaimType = ClaimConstants.Permission,
                    ClaimValue = p.Name,
                    CreatedBy = "RolePermissionSyncer",
                    CreatedOn = timeProvider.GetUtcNow(),
                })
                .ToList();
        }

        if (toAdd.Count == 0 && toRemove.Count == 0)
        {
            return 0;
        }

        if (toRemove.Count > 0)
        {
            context.RoleClaims.RemoveRange(toRemove);
        }

        if (toAdd.Count > 0)
        {
            await context.RoleClaims.AddRangeAsync(toAdd, cancellationToken).ConfigureAwait(false);
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "Synced '{Role}' for tenant '{Tenant}': added {Added} permission claim(s), removed {Removed} stale one(s)",
                roleName,
                tenantAccessor.MultiTenantContext.TenantInfo?.Id,
                toAdd.Count,
                toRemove.Count);
        }

        return toAdd.Count + toRemove.Count;
    }
}
