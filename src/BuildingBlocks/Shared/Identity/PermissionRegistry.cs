using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Boilerplate.BuildingBlocks.Shared.Constants;

/// <summary>
/// The host's permission catalog. Every permission is owned by the module that contributes it via
/// <see cref="PermissionRegistrationExtensions.AddPermissions"/>; the registry is built once from
/// those contributions when the container is built and is read-only from then on. Consumers (role
/// seeding, the permission catalog endpoint, the role editor's root filter) inject this, so what
/// they read cannot change under them mid-run.
/// </summary>
public interface IPermissionRegistry
{
    /// <summary>Every registered permission, de-duplicated by <see cref="AppPermission.Name"/>.</summary>
    IReadOnlyList<AppPermission> All { get; }

    /// <summary>Platform-scoped permissions — granted only to the root tenant's Admin role.</summary>
    IReadOnlyList<AppPermission> Root { get; }

    /// <summary>Everything that is not root-only — the grantable set for a tenant Admin.</summary>
    IReadOnlyList<AppPermission> Admin { get; }

    /// <summary>The subset every signed-in member gets through the Basic role.</summary>
    IReadOnlyList<AppPermission> Basic { get; }
}

/// <summary>
/// One module's contribution to the catalog. Registered as a DI singleton so the registry can be
/// built from <c>IEnumerable&lt;PermissionContribution&gt;</c> — which is fixed the moment the
/// container is built.
/// </summary>
public sealed class PermissionContribution
{
    public PermissionContribution(IEnumerable<AppPermission> permissions)
    {
        ArgumentNullException.ThrowIfNull(permissions);
        Permissions = [.. permissions];
    }

    public ImmutableArray<AppPermission> Permissions { get; }
}

public sealed class PermissionRegistry : IPermissionRegistry
{
    public PermissionRegistry(IEnumerable<PermissionContribution> contributions)
    {
        ArgumentNullException.ThrowIfNull(contributions);

        // DistinctBy keeps the first occurrence, so the first contributor of a name wins.
        All = [.. contributions.SelectMany(c => c.Permissions).DistinctBy(p => p.Name, StringComparer.Ordinal)];
        Root = [.. All.Where(p => p.IsRoot)];
        Admin = [.. All.Where(p => !p.IsRoot)];
        Basic = [.. All.Where(p => p.IsBasic)];
    }

    public ImmutableArray<AppPermission> All { get; }
    public ImmutableArray<AppPermission> Root { get; }
    public ImmutableArray<AppPermission> Admin { get; }
    public ImmutableArray<AppPermission> Basic { get; }

    IReadOnlyList<AppPermission> IPermissionRegistry.All => All;
    IReadOnlyList<AppPermission> IPermissionRegistry.Root => Root;
    IReadOnlyList<AppPermission> IPermissionRegistry.Admin => Admin;
    IReadOnlyList<AppPermission> IPermissionRegistry.Basic => Basic;
}

public static class PermissionRegistrationExtensions
{
    /// <summary>
    /// Contributes a module's permissions to the host catalog. Call it from the module's
    /// <c>ConfigureServices</c>; there is no way to add permissions after the container is built.
    /// </summary>
    public static IServiceCollection AddPermissions(this IServiceCollection services, IEnumerable<AppPermission> permissions)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton(new PermissionContribution(permissions));
        services.TryAddSingleton<IPermissionRegistry, PermissionRegistry>();

        return services;
    }
}

public record AppPermission(string Description, string Action, string Resource, bool IsBasic = false, bool IsRoot = false)
{
    public string Name => NameFor(Action, Resource);
    public static string NameFor(string action, string resource)
    {
        return $"Permissions.{resource}.{action}";
    }
}
