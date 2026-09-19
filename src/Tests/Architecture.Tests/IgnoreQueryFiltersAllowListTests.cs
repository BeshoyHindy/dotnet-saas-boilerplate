using Shouldly;
using System.Text.RegularExpressions;
using Xunit;

namespace Architecture.Tests;

/// <summary>
/// ADR-0002, "Persistence": <c>IgnoreQueryFilters()</c> is allowed only in a reviewed allow-list.
///
/// A <b>bare</b> <c>IgnoreQueryFilters()</c> strips <i>every</i> filter on the entity — including
/// Finbuckle's anonymous tenant filter, which is the whole of our isolation. It is the one call in
/// the codebase that can turn a tenant-scoped query into a cross-tenant one by accident, and it
/// reads like a soft-delete concern, so the mistake is easy and silent. EF Core 10 named filters
/// (see <c>QueryFilters</c>) let a call site lift only <c>SoftDelete</c> and keep the tenant filter;
/// prefer that, and reserve the bare form for genuinely cross-tenant work that re-pins the tenant
/// explicitly in the same query.
///
/// Source-scanning with exact expected counts, in the house style of
/// <see cref="AmbientTenantContextTests"/>: an unlisted file fails, a changed count fails (so a new
/// call in an already-blessed file still gets reviewed), and a stale entry fails (so the list cannot
/// rot into a list of files that once did something).
/// </summary>
public sealed partial class IgnoreQueryFiltersAllowListTests
{
    private static readonly string SolutionRoot = ModuleArchitectureTestsFixture.SolutionRoot;

    /// <summary>
    /// Files allowed to call <c>IgnoreQueryFilters</c>, with the exact number of calls expected and
    /// why each is safe. Adding an entry is a design decision: say, here and at the call site,
    /// whether the tenant filter is being lifted and what re-pins the tenant if so.
    /// </summary>
    private static readonly AllowedCall[] AllowedCalls =
    [
        // Generic escape hatch behind ISpecification.IgnoreQueryFilters. Unreachable today:
        // Specification<T> declares the flag get-only with no initializer and exposes no mutator,
        // so no shipped specification can turn it on. Kept listed (not deleted) because the
        // interface is public API; if a specification ever sets it, this count changes and the
        // reviewer is forced to look.
        new("src/BuildingBlocks/Persistence/Specifications/SpecificationEvaluator.cs", 1,
            "ISpecification escape hatch; no shipped specification can set the flag."),

        // Root operators resolving a subject in ANOTHER tenant (impersonation + operator token
        // exchange). The tenant filter must go — the caller is deliberately outside the target
        // tenant — and every query re-pins it with an explicit TenantId predicate. The UserRoles
        // read is pinned by the user id instead, which is a globally unique GUID, and the role
        // names it feeds are re-pinned by TenantId.
        new("src/Modules/Identity/Modules.Identity/Services/IdentityService.cs", 4,
            "Cross-tenant subject lookup for root operators; every query re-pins TenantId."),

        // Cross-tenant audit reads, gated by the IsRoot permission AuditTrails.ViewCrossTenant and
        // re-filtered to the single requested tenant. Without the bypass the requested tenant's
        // rows are invisible; without the re-filter it would return every tenant's.
        new("src/Modules/Auditing/Modules.Auditing/Features/v1/GetAudits/GetAuditsQueryHandler.cs", 1,
            "ViewCrossTenant-gated cross-tenant read, re-pinned to the requested TenantId."),
        new("src/Modules/Auditing/Modules.Auditing/Features/v1/GetAuditSummary/GetAuditSummaryQueryHandler.cs", 1,
            "ViewCrossTenant-gated cross-tenant read, re-pinned to the requested TenantId."),

        // Soft-delete-only lifts. These use the NAMED filter, so the tenant filter stays in force
        // and the query cannot reach another tenant's rows at all.
        new("src/Modules/Files/Modules.Files/Features/v1/ListTrashedFiles/ListTrashedFilesQueryHandler.cs", 1,
            "Named SoftDelete lift for the trash view; tenant filter stays on."),
        new("src/Modules/Files/Modules.Files/Features/v1/RestoreFile/RestoreFileCommandHandler.cs", 1,
            "Named SoftDelete lift to find the row being restored; tenant filter stays on."),
        new("src/Modules/Files/Modules.Files/Jobs/PurgeDeletedFilesJob.cs", 2,
            "Named SoftDelete lift inside a per-tenant ITenantScope pass; tenant filter stays on."),
    ];

    /// <summary>
    /// The bare form strips the tenant filter. Only the entries listed here may use it; everything
    /// else must name the filters it lifts, e.g. <c>IgnoreQueryFilters([QueryFilters.SoftDelete])</c>.
    /// </summary>
    private static readonly string[] AllowedBareCallFiles =
    [
        "src/BuildingBlocks/Persistence/Specifications/SpecificationEvaluator.cs",
        "src/Modules/Identity/Modules.Identity/Services/IdentityService.cs",
        "src/Modules/Auditing/Modules.Auditing/Features/v1/GetAudits/GetAuditsQueryHandler.cs",
        "src/Modules/Auditing/Modules.Auditing/Features/v1/GetAuditSummary/GetAuditSummaryQueryHandler.cs",
    ];

    [Fact]
    public void Only_AllowListed_Files_May_Call_IgnoreQueryFilters()
    {
        var expected = AllowedCalls.ToDictionary(c => c.File, c => c.ExpectedCalls, StringComparer.Ordinal);

        var offenders = ActualCallCounts()
            .Where(pair => !expected.ContainsKey(pair.Key))
            .Select(pair => $"{pair.Key} ({pair.Value} call(s))")
            .OrderBy(text => text, StringComparer.Ordinal)
            .ToList();

        offenders.ShouldBeEmpty(
            "ADR-0002: IgnoreQueryFilters() is allow-listed. A bare call also strips Finbuckle's " +
            "tenant filter. If you only want deleted rows, lift the named filter instead: " +
            "IgnoreQueryFilters([QueryFilters.SoftDelete]). If you genuinely need a cross-tenant " +
            "read, re-pin the tenant with an explicit predicate in the same query and add the file " +
            $"to {nameof(AllowedCalls)} with a reason. Offenders:\n  " +
            string.Join("\n  ", offenders));
    }

    [Fact]
    public void AllowListed_Files_Should_Have_The_Expected_Number_Of_Calls()
    {
        var actual = ActualCallCounts();

        var drift = AllowedCalls
            .Select(allowed =>
            {
                actual.TryGetValue(allowed.File, out int found);
                return (allowed, found);
            })
            .Where(pair => pair.found != pair.allowed.ExpectedCalls)
            .Select(pair =>
                $"{pair.allowed.File}: expected {pair.allowed.ExpectedCalls}, found {pair.found} " +
                $"({pair.allowed.Reason})")
            .OrderBy(text => text, StringComparer.Ordinal)
            .ToList();

        drift.ShouldBeEmpty(
            "the allow-list pins an exact call count per file so a new bypass in an already-blessed " +
            "file still gets reviewed, and so an entry whose calls are gone gets deleted rather than " +
            $"left as a standing privilege. Drift:\n  {string.Join("\n  ", drift)}");
    }

    [Fact]
    public void Bare_IgnoreQueryFilters_Should_Be_Confined_To_The_CrossTenant_CallSites()
    {
        var offenders = ProductionSourceFiles()
            .Select(file => (Relative: Relative(file), Bare: BareCallRegex().Count(Code(file))))
            .Where(pair => pair.Bare > 0)
            .Where(pair => !AllowedBareCallFiles.Contains(pair.Relative, StringComparer.Ordinal))
            .Select(pair => $"{pair.Relative} ({pair.Bare} bare call(s))")
            .OrderBy(text => text, StringComparer.Ordinal)
            .ToList();

        offenders.ShouldBeEmpty(
            "a bare IgnoreQueryFilters() removes the tenant filter too. Outside the reviewed " +
            "cross-tenant call sites, lift filters by name — IgnoreQueryFilters([QueryFilters.SoftDelete]) " +
            $"— so tenant isolation survives. Offenders:\n  {string.Join("\n  ", offenders)}");
    }

    [Fact]
    public void The_AllowList_Should_Not_Contain_Stale_Entries()
    {
        var stale = AllowedCalls
            .Where(allowed => !File.Exists(Absolute(allowed.File)))
            .Select(allowed => allowed.File)
            .Concat(AllowedBareCallFiles.Where(file => !File.Exists(Absolute(file))))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(text => text, StringComparer.Ordinal)
            .ToList();

        stale.ShouldBeEmpty(
            "an allow-list entry for a file that no longer exists is a privilege nobody is using — " +
            $"delete it, so the list keeps meaning something. Stale:\n  {string.Join("\n  ", stale)}");
    }

    private static Dictionary<string, int> ActualCallCounts()
    {
        return ProductionSourceFiles()
            .Select(file => (Relative: Relative(file), Count: CallRegex().Count(Code(file))))
            .Where(pair => pair.Count > 0)
            .ToDictionary(pair => pair.Relative, pair => pair.Count, StringComparer.Ordinal);
    }

    /// <summary>
    /// The file's code with comments removed. The doc comments on <c>ModelBuilderExtensions</c> and
    /// <c>QueryFilters</c> spell the call out on purpose — teaching the named-filter form is their
    /// job — and a scan that counted prose would punish the documentation.
    /// </summary>
    private static string Code(string file)
    {
        var text = File.ReadAllText(file);
        text = BlockCommentRegex().Replace(text, string.Empty);
        return LineCommentRegex().Replace(text, string.Empty);
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

    private static string Absolute(string relative) =>
        Path.Combine(SolutionRoot, relative.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>Any invocation — named or bare. The leading dot skips the property declarations on
    /// <c>ISpecification</c>/<c>Specification&lt;T&gt;</c> and the doc comments that mention the name.</summary>
    [GeneratedRegex(@"\.IgnoreQueryFilters\s*\(", RegexOptions.None, matchTimeoutMilliseconds: 5000)]
    private static partial Regex CallRegex();

    /// <summary>The argument-less form, which strips the tenant filter along with everything else.</summary>
    [GeneratedRegex(@"\.IgnoreQueryFilters\s*\(\s*\)", RegexOptions.None, matchTimeoutMilliseconds: 5000)]
    private static partial Regex BareCallRegex();

    [GeneratedRegex(@"/(bin|obj)/", RegexOptions.IgnoreCase)]
    private static partial Regex BuildOutputRegex();

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline, matchTimeoutMilliseconds: 5000)]
    private static partial Regex BlockCommentRegex();

    [GeneratedRegex(@"//[^\n]*", RegexOptions.None, matchTimeoutMilliseconds: 5000)]
    private static partial Regex LineCommentRegex();

    /// <param name="File">Path relative to the solution root, forward slashes.</param>
    /// <param name="ExpectedCalls">Exact number of <c>IgnoreQueryFilters</c> invocations in the file.</param>
    /// <param name="Reason">One line: why this bypass is safe.</param>
    private sealed record AllowedCall(string File, int ExpectedCalls, string Reason);
}
