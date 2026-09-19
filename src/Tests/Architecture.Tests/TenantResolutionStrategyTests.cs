using Finbuckle.MultiTenant.Abstractions;
using Shouldly;
using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;

namespace Architecture.Tests;

/// <summary>
/// ADR-0002 guard rails. The tenant is resolved from the signed token (or, for the
/// anonymous auth routes, from the <c>{tenant}</c> route value) and from nothing else.
/// Any caller-supplied tenant input — header, query string, host name, base path — is
/// deleted, not disabled, so this test bans the Finbuckle strategies that would
/// reintroduce one, and pins the production code to a single strategy implementation.
/// </summary>
public sealed partial class TenantResolutionStrategyTests
{
    private static readonly string SolutionRoot = ModuleArchitectureTestsFixture.SolutionRoot;

    /// <summary>
    /// Finbuckle strategy types and their registration extension methods that take the
    /// tenant from something the caller controls on the wire.
    /// </summary>
    private static readonly string[] BannedStrategyTokens =
    [
        "WithHeaderStrategy",
        "HeaderStrategy",
        "WithDelegateStrategy",
        "DelegateStrategy",
        "WithHostStrategy",
        "HostStrategy",
        "WithBasePathStrategy",
        "BasePathStrategy",
        "WithClaimStrategy",
        "ClaimStrategy",
        "WithRouteStrategy",
        "RouteStrategy",
        "WithSessionStrategy",
        "SessionStrategy",
        "WithRemoteAuthenticationCallbackStrategy",
    ];

    [Fact]
    public void Production_Sources_Should_Not_Reference_CallerSupplied_Tenant_Strategies()
    {
        var violations = new List<string>();

        foreach (var file in ProductionSourceFiles())
        {
            var text = File.ReadAllText(file);

            foreach (var token in BannedStrategyTokens)
            {
                if (!text.Contains(token, StringComparison.Ordinal))
                {
                    continue;
                }

                var relative = Path
                    .GetRelativePath(SolutionRoot, file)
                    .Replace('\\', '/');
                violations.Add($"{relative} references '{token}'");
            }
        }

        violations.ShouldBeEmpty(
            "ADR-0002: the tenant must come from the token claim (or the anonymous {tenant} route value) only. " +
            "Header / query / host / base-path / claim / route Finbuckle strategies are deleted, not disabled. " +
            $"Violations:\n  {string.Join("\n  ", violations)}");
    }

    [Fact]
    public void Exactly_One_MultiTenantStrategy_Implementation_Should_Exist()
    {
        var implementations = ModuleAssemblyDiscovery.GetModuleAssemblies()
            .SelectMany(SafeGetTypes)
            .Where(t => t.IsClass && !t.IsAbstract && typeof(IMultiTenantStrategy).IsAssignableFrom(t))
            .Select(t => t.FullName!)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        implementations.Count.ShouldBe(
            1,
            "ADR-0002 allows exactly one tenant resolution strategy. " +
            $"Found: {string.Join(", ", implementations)}");
    }

    /// <summary>
    /// Every C# source file that ships in the product: everything under <c>src/</c>
    /// except the test projects and build output.
    /// </summary>
    private static IEnumerable<string> ProductionSourceFiles()
    {
        var srcRoot = Path.Combine(SolutionRoot, "src");

        return Directory
            .EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories)
            .Where(f =>
            {
                var relative = Path.GetRelativePath(SolutionRoot, f).Replace('\\', '/');
                return !relative.StartsWith("src/Tests/", StringComparison.Ordinal)
                    && !BuildOutputRegex().IsMatch(relative);
            });
    }

    private static IEnumerable<Type> SafeGetTypes(Assembly assembly)
    {
        try { return assembly.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t is not null)!; }
    }

    [GeneratedRegex(@"/(bin|obj)/", RegexOptions.IgnoreCase)]
    private static partial Regex BuildOutputRegex();
}
