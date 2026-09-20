using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Boilerplate.DbMigrator;

/// <summary>
/// Reads what the shared module schema still owes.
///
/// There is one database for every tenant (#75), so "what is pending" is one question with one
/// answer per module <see cref="DbContext"/> — not one per tenant. The contexts are discovered by
/// scanning the module assemblies the migrator already loads, rather than listed here, so adding a
/// module does not add a fifth place to register it (see <c>.agents/rules/architecture.md</c>).
/// </summary>
internal static class ModuleSchema
{
    /// <summary>
    /// Pending migration names per module context, ordered by context name. The tenant catalog is
    /// excluded: <c>Program</c> reports it separately, before this runs.
    /// </summary>
    public static async Task<IReadOnlyList<(string Context, IReadOnlyList<string> Pending)>> GetPendingAsync(
        IServiceProvider services,
        IEnumerable<Assembly> moduleAssemblies,
        CancellationToken cancellationToken)
    {
        var results = new List<(string, IReadOnlyList<string>)>();

        foreach (var contextType in DiscoverContextTypes(moduleAssemblies))
        {
            if (services.GetService(contextType) is not DbContext context)
            {
                // Not registered in the migrator's reduced graph — nothing to migrate through it.
                continue;
            }

            var pending = (await context.Database.GetPendingMigrationsAsync(cancellationToken)
                .ConfigureAwait(false)).ToList();

            results.Add((contextType.Name, pending));
        }

        return [.. results.OrderBy(r => r.Item1, StringComparer.Ordinal)];
    }

    private static IEnumerable<Type> DiscoverContextTypes(IEnumerable<Assembly> moduleAssemblies) =>
        moduleAssemblies
            .Append(typeof(Boilerplate.BuildingBlocks.Eventing.Persistence.EventingDbContext).Assembly)
            .SelectMany(SafeGetTypes)
            .Where(t => t is { IsAbstract: false, IsGenericTypeDefinition: false }
                        && typeof(DbContext).IsAssignableFrom(t)
                        && t != typeof(Boilerplate.Modules.Multitenancy.Data.TenantDbContext))
            .Distinct();

    private static IEnumerable<Type> SafeGetTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t is not null)!;
        }
    }
}
