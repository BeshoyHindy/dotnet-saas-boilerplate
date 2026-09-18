using NetArchTest.Rules;
using Shouldly;
using Xunit;

namespace Architecture.Tests;

public class HostArchitectureTests
{
    [Fact]
    public void Modules_Should_Not_Depend_On_Hosts()
    {
        // Assemblies / namespaces that represent host applications.
        string[] hostNamespaces =
        {
            "Boilerplate.Api"
        };

        var result = Types
            .InCurrentDomain()
            .That()
            .ResideInNamespace("Boilerplate.Modules")
            .Should()
            .NotHaveDependencyOnAny(hostNamespaces)
            .GetResult();

        var failingTypes = result.FailingTypeNames ?? Array.Empty<string>();

        result.IsSuccessful.ShouldBeTrue(
            "Module code must not depend on host assemblies. " +
            $"Failing types: {string.Join(", ", failingTypes)}");
    }

    [Fact]
    public void Hosts_Should_Not_Depend_On_Module_Internals()
    {
        // Hosts may depend on module contracts and module root types,
        // but should not directly reference feature or data-layer namespaces.
        string[] forbiddenNamespaces =
        {
            "Boilerplate.Modules.Auditing.Features",
            "Boilerplate.Modules.Auditing.Data",
            "Boilerplate.Modules.Identity.Features",
            "Boilerplate.Modules.Identity.Data",
            "Boilerplate.Modules.Multitenancy.Features",
            "Boilerplate.Modules.Multitenancy.Data"
        };

        // Host namespaces only — `Boilerplate` alone is the shared root of every
        // assembly in the solution, so it must be narrowed to the host projects.
        var hostResult = Types
            .InCurrentDomain()
            .That()
            .ResideInNamespaceMatching(@"^Boilerplate\.(Api|AppHost|DbMigrator|Migrations)\b")
            .Should()
            .NotHaveDependencyOnAny(forbiddenNamespaces)
            .GetResult();

        var hostFailingTypes = hostResult.FailingTypeNames ?? Array.Empty<string>();

        hostResult.IsSuccessful.ShouldBeTrue(
            "Hosts should not depend directly on module feature or data internals. " +
            $"Failing types: {string.Join(", ", hostFailingTypes)}");
    }
}

internal static class ModuleArchitectureTestsFixture
{
    public static readonly string SolutionRoot = GetSolutionRoot();

    private static string GetSolutionRoot()
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());

        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src")))
        {
            directory = directory.Parent;
        }

        if (directory is null)
        {
            throw new InvalidOperationException("Unable to locate solution root containing 'src' folder.");
        }

        return directory.FullName;
    }
}
