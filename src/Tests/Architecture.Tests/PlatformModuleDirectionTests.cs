using Shouldly;
using System.Xml.Linq;
using Xunit;

namespace Architecture.Tests;

/// <summary>
/// Platform modules are the ones the template ships (ADR-0003); a product adds product modules of
/// its own. A product module may use a platform module's <c>.Contracts</c>, but a platform module
/// never references a product module, runtime or <c>.Contracts</c> (ADR-0010). That one-way rule is
/// what keeps the platform modules mergeable from the upstream template.
/// </summary>
public sealed class PlatformModuleDirectionTests
{
    private static readonly string[] PlatformModules =
        ["Identity", "Multitenancy", "Auditing", "Files", "Notifications"];

    private const string ModulePrefix = "Boilerplate.Modules.";

    [Fact]
    public void PlatformModules_Should_Not_Reference_Product_Modules()
    {
        string modulesRoot = Path.Combine(GetRepositoryRoot(), "src", "Modules");

        var projects = PlatformModules
            .SelectMany(module => Directory.GetFiles(
                Path.Combine(modulesRoot, module), "Boilerplate.Modules.*.csproj", SearchOption.AllDirectories))
            .ToArray();

        // Each platform module has a runtime project and a .Contracts project.
        projects.Length.ShouldBe(PlatformModules.Length * 2);

        var violations = new List<string>();
        foreach (string projectPath in projects)
        {
            string current = Path.GetFileNameWithoutExtension(projectPath);

            var referencedModules = XDocument.Load(projectPath)
                .Descendants("ProjectReference")
                .Select(x => ProjectReferences.GetReferencedProjectName((string?)x.Attribute("Include") ?? string.Empty))
                .Where(name => name.StartsWith(ModulePrefix, StringComparison.Ordinal));

            foreach (string referenced in referencedModules)
            {
                string module = referenced[ModulePrefix.Length..].Split('.')[0];
                if (!PlatformModules.Contains(module, StringComparer.Ordinal))
                {
                    violations.Add($"{current} -> {referenced}");
                }
            }
        }

        violations.ShouldBeEmpty(
            "A platform module (Identity, Multitenancy, Auditing, Files, Notifications) must not reference a " +
            "module outside that set, runtime or .Contracts: a product module depends on the platform, never " +
            "the reverse (ADR-0010). Remove the ProjectReference and turn the dependency around: the product " +
            "module calls the platform module's .Contracts, or handles an integration event the platform " +
            $"module publishes. Found: {string.Join("; ", violations)}");
    }

    private static string GetRepositoryRoot()
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());

        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src", "Modules")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Unable to locate the repository root containing 'src/Modules'.");
    }
}
