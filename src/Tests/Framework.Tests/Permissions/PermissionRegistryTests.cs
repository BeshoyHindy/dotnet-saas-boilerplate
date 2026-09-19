using Boilerplate.BuildingBlocks.Shared.Constants;
using Microsoft.Extensions.DependencyInjection;

namespace Framework.Tests.Permissions;

/// <summary>
/// The permission registry is built once from the module contributions registered during startup and
/// is read-only afterwards. It replaced a global mutable static: with that, any code path could push
/// a permission into the catalog long after the roles had been seeded from it, and tests could see
/// each other's registrations.
/// </summary>
public sealed class PermissionRegistryTests
{
    private static readonly AppPermission ModuleAView = new("View A", "View", "ModuleA", IsBasic: true);
    private static readonly AppPermission ModuleAAdmin = new("Admin A", "Manage", "ModuleA");
    private static readonly AppPermission ModuleBRoot = new("Root B", "View", "ModuleB", IsRoot: true);

    [Fact]
    public void Registry_Should_Union_Every_Module_Contribution()
    {
        var registry = BuildRegistry(services =>
        {
            services.AddPermissions([ModuleAView, ModuleAAdmin]);
            services.AddPermissions([ModuleBRoot]);
        });

        registry.All.Select(p => p.Name).ShouldBe(
            [ModuleAView.Name, ModuleAAdmin.Name, ModuleBRoot.Name],
            ignoreOrder: true);
    }

    [Fact]
    public void Registry_Should_Deduplicate_Contributions_By_Name()
    {
        var registry = BuildRegistry(services =>
        {
            services.AddPermissions([ModuleAView]);
            services.AddPermissions([ModuleAView, new AppPermission("Duplicate description", "View", "ModuleA")]);
        });

        registry.All.Count(p => p.Name == ModuleAView.Name).ShouldBe(1);
        registry.All.Single(p => p.Name == ModuleAView.Name).Description.ShouldBe("View A",
            "The first contributor wins, so a later duplicate cannot silently restate a permission.");
    }

    [Fact]
    public void Registry_Should_Partition_Permissions_Into_Root_Admin_And_Basic()
    {
        var registry = BuildRegistry(services => services.AddPermissions([ModuleAView, ModuleAAdmin, ModuleBRoot]));

        registry.Root.Select(p => p.Name).ShouldBe([ModuleBRoot.Name]);
        registry.Admin.Select(p => p.Name).ShouldBe([ModuleAView.Name, ModuleAAdmin.Name], ignoreOrder: true);
        registry.Basic.Select(p => p.Name).ShouldBe([ModuleAView.Name]);
    }

    [Fact]
    public void Registry_Should_Be_The_Same_Singleton_Instance_For_Every_Resolution()
    {
        var services = new ServiceCollection();
        services.AddPermissions([ModuleAView]);
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IPermissionRegistry>()
            .ShouldBeSameAs(provider.GetRequiredService<IPermissionRegistry>());
    }

    [Fact]
    public void Registry_Collections_Should_Reject_Mutation_After_Startup()
    {
        var registry = BuildRegistry(services => services.AddPermissions([ModuleAView, ModuleAAdmin, ModuleBRoot]));

        foreach (var collection in new[] { registry.All, registry.Root, registry.Admin, registry.Basic })
        {
            Should.Throw<NotSupportedException>(() => ((IList<AppPermission>)collection).Add(ModuleBRoot));
            Should.Throw<NotSupportedException>(() => ((IList<AppPermission>)collection).Clear());
        }
    }

    [Fact]
    public void Registry_Should_Ignore_Contributions_Registered_After_The_Provider_Is_Built()
    {
        var services = new ServiceCollection();
        services.AddPermissions([ModuleAView]);

        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<IPermissionRegistry>();

        // Late registration — the shape the old static `Register(...)` allowed from anywhere.
        services.AddPermissions([ModuleBRoot]);

        registry.All.Select(p => p.Name).ShouldBe([ModuleAView.Name]);
        provider.GetRequiredService<IPermissionRegistry>().All.Select(p => p.Name).ShouldBe([ModuleAView.Name]);
    }

    private static IPermissionRegistry BuildRegistry(Action<IServiceCollection> configure)
    {
        var services = new ServiceCollection();
        configure(services);
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IPermissionRegistry>();
    }
}
