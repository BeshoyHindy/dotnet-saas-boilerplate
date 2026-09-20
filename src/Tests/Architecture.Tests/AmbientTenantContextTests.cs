using Shouldly;
using System.Text.RegularExpressions;
using Xunit;

namespace Architecture.Tests;

/// <summary>
/// ADR-0002, "Jobs and events". Writing Finbuckle's ambient tenant context is a one-file privilege.
///
/// The pattern this bans is "create a DI scope, then set <c>IMultiTenantContextSetter</c> on it",
/// which was hand-rolled in eight places and is wrong in all of them: a <c>MultiTenantDbContext</c>
/// captures its <c>TenantInfo</c> — and with it the tenant filter — at construction, so a context
/// resolved from a scope created before the tenant is installed reads every tenant's rows through a
/// null tenant filter. <c>AmbientTenantContext</c> is the only writer;
/// <c>ITenantScope</c> is how everything else enters a tenant, and it gets the order right by
/// construction.
///
/// Source-scanning rather than reflection: the point is that the <i>text</i> does not reappear in a
/// new file, and an allow-list entry that stops matching has to fail so the list cannot rot.
/// </summary>
public sealed partial class AmbientTenantContextTests
{
    private static readonly string SolutionRoot = ModuleArchitectureTestsFixture.SolutionRoot;

    private const string SetterToken = "IMultiTenantContextSetter";

    /// <summary>
    /// Production files allowed to name the setter, relative to the solution root.
    /// Adding an entry here is a design decision, not a formality: say why in the file itself.
    /// </summary>
    private static readonly string[] AllowedFiles =
    [
        // The single writer. Everything else goes through ITenantScope, which goes through this.
        "src/BuildingBlocks/Shared/Multitenancy/AmbientTenantContext.cs",
    ];

    [Fact]
    public void Only_AmbientTenantContext_May_Write_The_Finbuckle_Tenant_Context()
    {
        var offenders = ProductionSourceFiles()
            .Where(file => File.ReadAllText(file).Contains(SetterToken, StringComparison.Ordinal))
            .Select(Relative)
            .Where(relative => !AllowedFiles.Contains(relative, StringComparer.Ordinal))
            .OrderBy(relative => relative, StringComparer.Ordinal)
            .ToList();

        offenders.ShouldBeEmpty(
            $"ADR-0002: only AmbientTenantContext may set the ambient tenant. Enter a tenant through " +
            $"ITenantScope (which opens the tenant scope before the DI scope, with the full record from " +
            $"the store) instead of setting {SetterToken} by hand. Offenders:\n  " +
            string.Join("\n  ", offenders));
    }

    [Fact]
    public void The_AllowList_Should_Not_Contain_Stale_Entries()
    {
        var stale = AllowedFiles
            .Where(relative =>
            {
                var absolute = Path.Combine(SolutionRoot, relative.Replace('/', Path.DirectorySeparatorChar));
                return !File.Exists(absolute)
                    || !File.ReadAllText(absolute).Contains(SetterToken, StringComparison.Ordinal);
            })
            .ToList();

        stale.ShouldBeEmpty(
            "an allow-list entry that no longer references the setter is a privilege nobody is using — " +
            $"delete it, so the list keeps meaning something. Stale:\n  {string.Join("\n  ", stale)}");
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
                var relative = Relative(f);
                return !relative.StartsWith("src/Tests/", StringComparison.Ordinal)
                    && !BuildOutputRegex().IsMatch(relative);
            });
    }

    private static string Relative(string file) =>
        Path.GetRelativePath(SolutionRoot, file).Replace('\\', '/');

    [GeneratedRegex(@"/(bin|obj)/", RegexOptions.IgnoreCase)]
    private static partial Regex BuildOutputRegex();
}
