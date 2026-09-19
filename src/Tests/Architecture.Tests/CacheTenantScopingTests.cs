using Boilerplate.BuildingBlocks.Caching;
using Shouldly;
using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;

namespace Architecture.Tests;

/// <summary>
/// ADR-0002, issue #77: cache keys are tenant-prefixed by the building block, not by caller
/// convention. Nineteen call sites across six files used to compose the tenant into the key
/// themselves; once the block does it, a leftover hand-built prefix does not merely look untidy —
/// it doubles up (<c>t:acme:theme:t:acme</c>) and re-opens the "pass someone else's id" hole the
/// prefix closes.
///
/// Source-scanning and reflection rather than a type graph: the point is that the <i>text</i> does
/// not reappear in a new file, and an allow-list entry that stops matching has to fail so the list
/// cannot rot — the same shape as <see cref="AmbientTenantContextTests"/>.
/// </summary>
public sealed partial class CacheTenantScopingTests
{
    private static readonly string SolutionRoot = ModuleArchitectureTestsFixture.SolutionRoot;

    /// <summary>The building block itself: the one place allowed to know the physical key format.</summary>
    private const string CachingBlock = "src/BuildingBlocks/Caching/";

    #region (a) No production code composes a tenant into a cache key

    /// <summary>
    /// The catalogue is where keys come from, so holding it tenant-free is the load-bearing half of
    /// rule (a): with no tenant-parameterised member to call, a hand-built prefix would have to be an
    /// inline string, which the next test bans.
    /// </summary>
    [Fact]
    public void CacheKeys_Should_Expose_No_TenantParameterised_Member()
    {
        var offenders = new List<string>();

        foreach (var type in new[] { typeof(CacheKeys), typeof(CacheKeys.Tags) })
        {
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                // "TenantTheme" names the entry, not a parameter — only flag it if it takes one.
                if (TenantWordRegex().IsMatch(method.Name) && method.GetParameters().Length > 0)
                {
                    offenders.Add($"{type.Name}.{method.Name}(...) — a key method named for a tenant that takes arguments");
                }

                offenders.AddRange(method.GetParameters()
                    .Where(p => TenantWordRegex().IsMatch(p.Name ?? string.Empty))
                    .Select(p => $"{type.Name}.{method.Name} takes '{p.Name}'"));
            }
        }

        offenders.ShouldBeEmpty(
            "CacheKeys must expose only tenant-less logical names — the cache supplies the tenant from the " +
            "ambient context (CacheKeyScope). A key that also takes a tenant id lets a caller name someone " +
            "else's partition, which is exactly what ADR-0002 removes. Offenders:\n  " +
            string.Join("\n  ", offenders));
    }

    /// <summary>
    /// A cache call whose key or tag is written inline is how a tenant gets interpolated back into a
    /// key. Keys live in <c>CacheKeys</c>; this makes that a rule rather than a habit.
    /// </summary>
    /// <remarks>
    /// Limits, stated so nobody mistakes this for more than it is: it matches a literal or an
    /// interpolated string appearing directly as the first argument of a cache call. A key assembled
    /// into a local variable a few lines earlier, or returned from a helper, is invisible to it. The
    /// reflection test above is what closes that gap for the keys the kit actually uses, since they
    /// all come from <c>CacheKeys</c>.
    /// </remarks>
    [Fact]
    public void No_Production_Code_Outside_TheBlock_Should_Inline_A_CacheKey_Or_Tag()
    {
        var offenders = new List<string>();

        foreach (var file in ProductionSourceFiles().Where(f => !Relative(f).StartsWith(CachingBlock, StringComparison.Ordinal)))
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (IsComment(lines[i]))
                {
                    continue;
                }

                if (InlineCacheKeyRegex().IsMatch(lines[i]))
                {
                    offenders.Add($"{Relative(file)}:{i + 1}  {lines[i].Trim()}");
                }
            }
        }

        offenders.ShouldBeEmpty(
            "cache keys and tags belong in CacheKeys, as tenant-less logical names — an inline string at the " +
            "call site is how a tenant id gets interpolated back into a key that the block already prefixes. " +
            "Offenders:\n  " + string.Join("\n  ", offenders));
    }

    #endregion

    #region (b) Nobody outside the block reaches the undecorated cache

    /// <summary>
    /// <c>HybridCache</c> resolves to the tenant-scoped decorator and <c>GlobalHybridCache</c> to the
    /// explicitly global one. Registering another <c>AddHybridCache</c>, or naming the telemetry
    /// decorator, would hand someone a cache with no prefix at all.
    /// </summary>
    [Fact]
    public void Only_TheBlock_May_Register_Or_Name_TheUndecorated_Cache()
    {
        string[] tokens = ["AddHybridCache", "ObservableHybridCache", "TenantScopedHybridCache"];

        var offenders = ProductionSourceFiles()
            .Select(Relative)
            .Where(relative => !relative.StartsWith(CachingBlock, StringComparison.Ordinal))
            .Where(relative => ContainsOutsideComments(
                Path.Combine(SolutionRoot, relative.Replace('/', Path.DirectorySeparatorChar)), tokens))
            .OrderBy(relative => relative, StringComparer.Ordinal)
            .ToList();

        offenders.ShouldBeEmpty(
            "the decorator stack is the block's business: inject HybridCache (tenant-scoped) or " +
            "GlobalHybridCache (explicitly tenant-less). Building or naming the inner cache bypasses the " +
            "tenant prefix entirely. Offenders:\n  " + string.Join("\n  ", offenders));
    }

    #endregion

    #region (c) IDistributedCache is an allow-list

    /// <summary>
    /// Talking to L2 directly skips the prefix, the telemetry and the tag bookkeeping. Three places
    /// have a reason to; everything else uses <c>HybridCache</c>. Say why in the file, not only here.
    /// </summary>
    private static readonly string[] DistributedCacheAllowList =
    [
        // HybridCache has no get-only probe (dotnet/aspnetcore#57191), so the replay check reads L2
        // by key — the physical key, which it asks CacheKeyScope for rather than rebuilding.
        "src/BuildingBlocks/Web/Idempotency/IdempotencyEndpointFilter.cs",

        // A liveness probe for the Redis connection itself, not an application entry.
        "src/BuildingBlocks/Web/Health/RedisHealthCheck.cs",
    ];

    [Fact]
    public void Only_AllowListed_Files_May_Touch_IDistributedCache()
    {
        const string Token = "IDistributedCache";

        var offenders = ProductionSourceFiles()
            .Select(Relative)
            .Where(relative => !relative.StartsWith(CachingBlock, StringComparison.Ordinal))
            .Where(relative => ContainsOutsideComments(
                Path.Combine(SolutionRoot, relative.Replace('/', Path.DirectorySeparatorChar)), [Token]))
            .Where(relative => !DistributedCacheAllowList.Contains(relative, StringComparer.Ordinal))
            .OrderBy(relative => relative, StringComparer.Ordinal)
            .ToList();

        offenders.ShouldBeEmpty(
            "reaching past HybridCache to IDistributedCache skips the tenant prefix, so an application entry " +
            "written that way lands in the shared key space ADR-0002 forbids. If a new caller genuinely has " +
            "to, add it to DistributedCacheAllowList and explain why in the file. Offenders:\n  " +
            string.Join("\n  ", offenders));
    }

    [Fact]
    public void The_DistributedCache_AllowList_Should_Not_Contain_Stale_Entries()
    {
        var stale = DistributedCacheAllowList
            .Where(relative =>
            {
                var absolute = Path.Combine(SolutionRoot, relative.Replace('/', Path.DirectorySeparatorChar));
                return !File.Exists(absolute) || !ContainsOutsideComments(absolute, ["IDistributedCache"]);
            })
            .ToList();

        stale.ShouldBeEmpty(
            "an allow-list entry that no longer touches IDistributedCache is a privilege nobody is using — " +
            $"delete it, so the list keeps meaning something. Stale:\n  {string.Join("\n  ", stale)}");
    }

    #endregion

    #region helpers

    private static bool ContainsOutsideComments(string file, IEnumerable<string> tokens)
        => File.ReadLines(file).Any(line =>
            !IsComment(line) && tokens.Any(token => line.Contains(token, StringComparison.Ordinal)));

    /// <summary>
    /// Comment lines are skipped throughout. XML docs name these types constantly — on purpose, since
    /// explaining the rule is how it survives — and a test that fired on prose would teach people to
    /// stop writing it.
    /// </summary>
    private static bool IsComment(string line)
    {
        var trimmed = line.TrimStart();
        return trimmed.StartsWith("//", StringComparison.Ordinal)
            || trimmed.StartsWith('*')
            || trimmed.StartsWith("/*", StringComparison.Ordinal);
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

    [GeneratedRegex(@"[Tt]enant", RegexOptions.CultureInvariant)]
    private static partial Regex TenantWordRegex();

    /// <summary>
    /// A cache call whose first argument is a string literal or an interpolated string.
    /// </summary>
    [GeneratedRegex(
        @"\.(GetOrCreateAsync|SetAsync|RemoveAsync|RemoveByTagAsync)\s*(<[^()]*>)?\s*\(\s*\$?""",
        RegexOptions.CultureInvariant)]
    private static partial Regex InlineCacheKeyRegex();

    #endregion
}
