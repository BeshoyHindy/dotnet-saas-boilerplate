using Shouldly;
using System.Text.RegularExpressions;
using Xunit;

namespace Architecture.Tests;

/// <summary>
/// ADR-0002: storage object keys are tenant-prefixed <b>by the building block</b>, not by caller
/// convention. Naming a key root or a tenant id outside the block is the shape of the arrangement
/// this replaced — one that produced correct keys and guaranteed nothing, because nothing refused a
/// key built any other way.
///
/// <para><b>What this scan can and cannot see.</b> It is text, over production sources under
/// <c>src/</c>. It catches the two forms a hand-built key actually takes: a string literal that
/// starts with one of the block's roots, and an interpolated string that starts with a tenant hole.
/// It does not see a key assembled with <c>string.Concat</c>, <c>Path.Combine</c>, a
/// differently-named variable, or a root spelled in pieces — and it is not trying to. The guarantee
/// is the runtime one: <c>IStorageService</c> refuses every key it did not compose, so a key built
/// any other way fails at the first operation rather than quietly addressing the wrong tenant. This
/// test is here to keep the habit visible in review, and to keep the allow-list honest.</para>
///
/// <para>Deploy manifests and docs are out of scope on purpose: the bucket policy in
/// <c>deploy/</c> has to name <c>uploads/</c>, and its own contract test pins it.</para>
/// </summary>
public sealed partial class StorageKeyOwnershipTests
{
    private static readonly string SolutionRoot = ModuleArchitectureTestsFixture.SolutionRoot;

    /// <summary>A string literal beginning with one of the block's key roots.</summary>
    [GeneratedRegex(@"""(?:tenants|uploads)/", RegexOptions.CultureInvariant)]
    private static partial Regex KeyRootLiteral();

    /// <summary>An interpolated string whose first hole looks like a tenant id, followed by a separator.</summary>
    [GeneratedRegex(@"\$""\{[^}""]*[Tt]enant[^}""]*\}/", RegexOptions.CultureInvariant)]
    private static partial Regex TenantIdIntoPath();

    /// <summary>
    /// Production files allowed to write a key root, relative to the solution root. An entry here
    /// is a design decision: it says "this file <i>is</i> the block's key layer".
    /// </summary>
    private static readonly string[] AllowedFiles =
    [
        // The single owner of both roots. Everything else asks it for a key.
        "src/BuildingBlocks/Storage/Keys/TenantStorageKeyRules.cs",
    ];

    [Fact]
    public void Only_The_Storage_Block_May_Write_A_Key_Root()
    {
        var offenders = ProductionSourceFiles()
            .Where(file => KeyRootLiteral().IsMatch(File.ReadAllText(file)))
            .Select(Relative)
            .Where(relative => !AllowedFiles.Contains(relative, StringComparer.Ordinal))
            .OrderBy(relative => relative, StringComparer.Ordinal)
            .ToList();

        offenders.ShouldBeEmpty(
            "ADR-0002: the Storage block owns the `tenants/` and `uploads/tenants/` roots. Ask it for " +
            "a key — IStorageService.ComposeKey(space, relativePath) or UploadAsync — and persist what " +
            "it returns. Offenders:\n  " + string.Join("\n  ", offenders));
    }

    [Fact]
    public void No_Production_Code_May_Interpolate_A_Tenant_Id_Into_A_Storage_Key()
    {
        var offenders = ProductionSourceFiles()
            .Where(file => TenantIdIntoPath().IsMatch(File.ReadAllText(file)))
            .Select(Relative)
            .Where(relative => !AllowedFiles.Contains(relative, StringComparer.Ordinal))
            .OrderBy(relative => relative, StringComparer.Ordinal)
            .ToList();

        offenders.ShouldBeEmpty(
            "A path that starts with a tenant id is a storage key being built by hand. The block " +
            "prefixes the tenant; callers pass a tenant-relative path. Offenders:\n  " +
            string.Join("\n  ", offenders));
    }

    [Fact]
    public void The_AllowList_Should_Not_Contain_Stale_Entries()
    {
        var stale = AllowedFiles
            .Where(relative =>
            {
                var absolute = Path.Combine(SolutionRoot, relative.Replace('/', Path.DirectorySeparatorChar));
                return !File.Exists(absolute) || !KeyRootLiteral().IsMatch(File.ReadAllText(absolute));
            })
            .ToList();

        stale.ShouldBeEmpty(
            "an allow-list entry that no longer writes a key root is a privilege nobody is using — " +
            $"delete it, so the list keeps meaning something. Stale:\n  {string.Join("\n  ", stale)}");
    }

    /// <summary>
    /// Every C# source file that ships in the product: everything under <c>src/</c> except the test
    /// projects and build output.
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
