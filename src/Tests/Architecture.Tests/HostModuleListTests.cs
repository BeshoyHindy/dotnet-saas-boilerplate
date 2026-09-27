using Boilerplate.BuildingBlocks.Web.Modules;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using System.Linq.Expressions;
using System.Reflection;
using Xunit;

namespace Architecture.Tests;

/// <summary>
/// Each host keeps two module lists: the modules it loads (<c>HostModules.All</c>) and the
/// assemblies Mediator's source generator scans for handlers (<c>o.Assemblies</c> in
/// <c>Program.cs</c>). A module missing from either used to fail silently (it never loaded, or its
/// handlers were never found). These tests compare both lists, in both hosts, with every assembly
/// that carries <c>[AppModule]</c>.
/// </summary>
public sealed class HostModuleListTests
{
    public static TheoryData<string> Hosts => new() { "Api", "DbMigrator" };

    [Theory]
    [MemberData(nameof(Hosts))]
    public void HostModules_Should_List_Every_AppModule_Assembly(string host)
    {
        var target = HostUnderTest.For(host);
        var listed = target.HostModules.Select(a => a.GetName().Name).ToHashSet(StringComparer.Ordinal);

        var missing = AppModuleAssemblies()
            .Where(a => !listed.Contains(a.GetName().Name))
            .Select(a => $"{a.GetName().Name} (add `typeof({a.GetCustomAttribute<AppModuleAttribute>()!.ModuleType.FullName}).Assembly`)")
            .ToArray();

        missing.ShouldBeEmpty(
            $"A module carrying [AppModule] is missing from HostModules.All in {target.HostModulesFile}, so the " +
            $"{host} host never loads it: {string.Join("; ", missing)}.");
    }

    [Theory]
    [MemberData(nameof(Hosts))]
    public void MediatorAssemblies_Should_Register_Every_AppModule_Handlers(string host)
    {
        var target = HostUnderTest.For(host);
        var registered = target.MediatorRegistrations()
            .Select(d => d.ImplementationType?.Assembly)
            .OfType<Assembly>()
            .ToHashSet();

        var modulesWithHandlers = AppModuleAssemblies()
            .Where(a => HandlerTypes(a).Any())
            .ToArray();

        // Every platform module has handlers today; if none did, this test would pass vacuously.
        modulesWithHandlers.ShouldNotBeEmpty();

        var missing = modulesWithHandlers
            .Where(a => !registered.Contains(a))
            .Select(a => a.GetName().Name!)
            .ToArray();

        missing.ShouldBeEmpty(
            $"{string.Join(", ", missing)} has Mediator handlers, but the {host} host registers none of " +
            $"them: the module is missing from the `o.Assemblies` list of AddMediator in {target.ProgramFile}. " +
            "Add a type from its runtime assembly and one from its .Contracts assembly.");
    }

    private static Assembly[] AppModuleAssemblies()
    {
        var assemblies = ModuleAssemblyDiscovery.GetModuleAssemblies()
            .Where(a => a.GetCustomAttributes<AppModuleAttribute>().Any())
            .ToArray();

        assemblies.ShouldNotBeEmpty("No assembly carrying [AppModule] was found next to Architecture.Tests.");
        return assemblies;
    }

    private static IEnumerable<Type> HandlerTypes(Assembly assembly) =>
        assembly.GetTypes().Where(t =>
            t is { IsClass: true, IsAbstract: false, IsGenericTypeDefinition: false }
            && t.GetInterfaces().Any(i =>
                i.IsGenericType
                && string.Equals(i.Namespace, "Mediator", StringComparison.Ordinal)
                && i.Name.Contains("Handler`", StringComparison.Ordinal)));

    private sealed record HostUnderTest(
        Assembly Assembly,
        IReadOnlyList<Assembly> HostModules,
        string ProgramFile,
        string HostModulesFile)
    {
        public static HostUnderTest For(string host) => host switch
        {
            "Api" => new(
                typeof(Boilerplate.Api.HostModules).Assembly,
                Boilerplate.Api.HostModules.All,
                "src/Host/Boilerplate.Api/Program.cs",
                "src/Host/Boilerplate.Api/HostModules.cs"),
            "DbMigrator" => new(
                typeof(Boilerplate.DbMigrator.HostModules).Assembly,
                Boilerplate.DbMigrator.HostModules.All,
                "src/Host/Boilerplate.DbMigrator/Program.cs",
                "src/Host/Boilerplate.DbMigrator/HostModules.cs"),
            _ => throw new ArgumentOutOfRangeException(nameof(host), host, "Unknown host."),
        };

        /// <summary>
        /// Calls the host's own generated <c>AddMediator</c> on an empty service collection. The
        /// generator bakes <c>o.Assemblies</c> into that method at compile time, so what it
        /// registers is exactly what the list in <c>Program.cs</c> covers. Both hosts generate
        /// types with the same names, hence reflection instead of naming them.
        /// </summary>
        public ServiceCollection MediatorRegistrations()
        {
            var extensions = Assembly.GetType(
                "Microsoft.Extensions.DependencyInjection.MediatorDependencyInjectionExtensions",
                throwOnError: true)!;
            var optionsType = Assembly.GetType("Mediator.MediatorOptions", throwOnError: true)!;
            var configureType = typeof(Action<>).MakeGenericType(optionsType);

            var addMediator = extensions.GetMethod(
                "AddMediator",
                [typeof(IServiceCollection), configureType])!;

            // The generated method refuses a lifetime other than the one it was generated for.
            var options = Expression.Parameter(optionsType, "o");
            var configure = Expression.Lambda(
                configureType,
                Expression.Assign(
                    Expression.Property(options, "ServiceLifetime"),
                    Expression.Constant(ServiceLifetime.Scoped)),
                options).Compile();

            var services = new ServiceCollection();
            addMediator.Invoke(null, [services, configure]);
            return services;
        }
    }
}
