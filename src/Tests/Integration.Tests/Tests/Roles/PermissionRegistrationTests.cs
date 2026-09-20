using Boilerplate.BuildingBlocks.Shared.Constants;
using Boilerplate.Modules.Auditing.Contracts.Authorization;
using Boilerplate.Modules.Files.Contracts.Authorization;
using Boilerplate.Modules.Identity.Contracts.Authorization;
using Boilerplate.Modules.Multitenancy.Contracts.Authorization;
using Boilerplate.Modules.Notifications.Contracts.Authorization;
using Integration.Tests.Infrastructure;
using Integration.Tests.Infrastructure.Extensions;

namespace Integration.Tests.Tests.Roles;

/// <summary>
/// Regression coverage for the per-module permission registry. Each module owns its
/// permissions in its Contracts project and contributes them via <c>services.AddPermissions(...)</c>
/// during <c>ConfigureServices</c>. These tests catch two classes of bug:
///   1. A new permission added to a module is not registered (registry drift).
///   2. The Admin role's claim seeding is not propagating new permissions to existing tenants.
/// </summary>
[Collection(AppCollectionDefinition.Name)]
public sealed class PermissionRegistrationTests
{
    private readonly AuthHelper _auth;
    private readonly AppWebApplicationFactory _factory;

    public PermissionRegistrationTests(AppWebApplicationFactory factory)
    {
        _auth = new AuthHelper(factory);
        _factory = factory;
    }

    [Fact]
    public void Registry_Should_Contain_All_Module_Permissions()
    {
        var registry = _factory.Services.GetRequiredService<IPermissionRegistry>();
        var registered = registry.All.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

        // Each module's static `All` list is the single source of truth.
        AssertAllRegistered(registered, IdentityPermissions.All.Select(p => p.Name), nameof(IdentityPermissions));
        AssertAllRegistered(registered, MultitenancyPermissions.All.Select(p => p.Name), nameof(MultitenancyPermissions));
        AssertAllRegistered(registered, AuditingPermissions.All.Select(p => p.Name), nameof(AuditingPermissions));
        AssertAllRegistered(registered, FilesPermissions.All.Select(p => p.Name), nameof(FilesPermissions));
        AssertAllRegistered(registered, NotificationPermissions.All.Select(p => p.Name), nameof(NotificationPermissions));
        AssertAllRegistered(registered, SystemPermissions.All.Select(p => p.Name), nameof(SystemPermissions));
    }

    [Fact]
    public async Task RootAdmin_Should_Have_All_Files_Permissions_After_Seed()
    {
        using var client = await _auth.CreateRootAdminClientAsync();

        var response = await client.GetAsync($"{TestConstants.IdentityBasePath}/permissions");
        var permissions = await response.DeserializeAsync<string[]>();

        var expected = new[]
        {
            FilesPermissions.Upload,
            FilesPermissions.DeleteOwn,
            FilesPermissions.DeleteAny,
            FilesPermissions.ViewTrash,
            FilesPermissions.Restore,
            NotificationPermissions.Inbox.View,
            NotificationPermissions.Inbox.MarkRead,
        };

        var permSet = permissions.ToHashSet(StringComparer.Ordinal);
        foreach (var perm in expected)
        {
            permSet.ShouldContain(perm, $"Admin role missing expected permission '{perm}'");
        }
    }

    [Fact]
    public async Task RootAdmin_Should_Have_Cross_Module_Permissions()
    {
        using var client = await _auth.CreateRootAdminClientAsync();

        var response = await client.GetAsync($"{TestConstants.IdentityBasePath}/permissions");
        var permissions = await response.DeserializeAsync<string[]>();
        var permSet = permissions.ToHashSet(StringComparer.Ordinal);

        // Spot-check one perm from each module — covers the "new module added but seeding not run" case.
        permSet.ShouldContain(IdentityPermissions.Users.View);
        permSet.ShouldContain(FilesPermissions.Upload);
        permSet.ShouldContain(AuditingPermissions.AuditTrails.View);
        permSet.ShouldContain(SystemPermissions.Dashboard.View);

        // Tenants permissions are root-only — admin@root.com on the root tenant gets them.
        permSet.ShouldContain(MultitenancyPermissions.Tenants.View);
    }

    private static void AssertAllRegistered(HashSet<string> registered, IEnumerable<string> expected, string moduleName)
    {
        var missing = expected.Where(name => !registered.Contains(name)).ToList();
        missing.ShouldBeEmpty(
            $"{moduleName}: {missing.Count} permission(s) not in the host's IPermissionRegistry — " +
            $"missing: [{string.Join(", ", missing)}]. " +
            $"Make sure the module's ConfigureServices calls services.AddPermissions({moduleName}.All).");
    }
}
