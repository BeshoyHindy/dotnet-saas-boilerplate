using Boilerplate.Modules.Auditing;
using Boilerplate.Modules.Identity;
using Boilerplate.Modules.Multitenancy;
using System.Reflection;

namespace Generic.Tests.Architecture;

/// <summary>
/// Discovers all Boilerplate module assemblies for use in generic architecture tests.
/// </summary>
internal static class ModuleAssemblyDiscovery
{
    private static readonly Assembly[] _cached = Discover();

    public static Assembly[] GetModuleAssemblies() => _cached;

    private static Assembly[] Discover()
    {
        // Force-load seed assemblies
        _ = typeof(AuditingModule);
        _ = typeof(IdentityModule);
        _ = typeof(MultitenancyModule);

        return AppDomain.CurrentDomain
            .GetAssemblies()
            .Where(a =>
            {
                var name = a.GetName().Name ?? string.Empty;
                return name.StartsWith("Boilerplate.Modules.", StringComparison.Ordinal)
                       && !name.EndsWith(".Contracts", StringComparison.Ordinal);
            })
            .OrderBy(a => a.GetName().Name, StringComparer.Ordinal)
            .ToArray();
    }
}
