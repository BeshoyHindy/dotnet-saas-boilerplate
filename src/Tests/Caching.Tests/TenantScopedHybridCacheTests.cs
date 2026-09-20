using Boilerplate.BuildingBlocks.Caching;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Caching.Tests;

/// <summary>
/// The tenant prefix is the building block's job (ADR-0002, issue #77). These tests pin the three
/// promises that makes: the same logical key in two tenants is two physical entries; a tag
/// invalidation in one tenant cannot reach another's; and a cache call with no tenant at all fails
/// loudly rather than quietly landing somewhere shared.
///
/// Physical keys are asserted against the L2 <see cref="IDistributedCache"/> rather than against a
/// string the test builds itself — that is the byte the next process actually reads, and it is the
/// same thing the idempotency probe looks up.
/// </summary>
public sealed class TenantScopedHybridCacheTests
{
    private const string TenantA = "alpha";
    private const string TenantB = "beta";

    private sealed class MutableTenantAccessor : ICacheTenantAccessor
    {
        public string? TenantId { get; set; }
    }

    /// <summary>
    /// A minimal L2 that records the keys it is handed. It exists because HybridCache deliberately
    /// ignores <c>MemoryDistributedCache</c> as a backend (it would just duplicate L1), so the
    /// in-memory fallback the other unit tests use writes nothing we could inspect. Anything else
    /// implementing <see cref="IDistributedCache"/> is treated as a real L2 — which is exactly what
    /// a Redis deployment looks like, and what the idempotency probe reads.
    /// </summary>
    private sealed class RecordingDistributedCache : IDistributedCache
    {
        private readonly Dictionary<string, byte[]> _entries = new(StringComparer.Ordinal);

        public byte[]? Get(string key)
        {
            lock (_entries) { return _entries.TryGetValue(key, out var value) ? value : null; }
        }

        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => Task.FromResult(Get(key));

        public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
        {
            lock (_entries) { _entries[key] = value; }
        }

        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
        {
            Set(key, value, options);
            return Task.CompletedTask;
        }

        public void Refresh(string key) { }

        public Task RefreshAsync(string key, CancellationToken token = default) => Task.CompletedTask;

        public void Remove(string key)
        {
            lock (_entries) { _entries.Remove(key); }
        }

        public Task RemoveAsync(string key, CancellationToken token = default)
        {
            Remove(key);
            return Task.CompletedTask;
        }
    }

    private sealed record Harness(
        HybridCache Cache,
        GlobalHybridCache Global,
        CacheKeyScope Scope,
        RecordingDistributedCache L2,
        MutableTenantAccessor Tenant,
        ServiceProvider Provider) : IDisposable
    {
        public void Dispose() => Provider.Dispose();
    }

    private static Harness Build()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder().Build();
        var accessor = new MutableTenantAccessor();
        var l2 = new RecordingDistributedCache();
        services.AddSingleton<IConfiguration>(config);
        services.AddSingleton<ICacheTenantAccessor>(accessor);
        // Registered first: AddHeroCaching's in-memory fallback is a TryAdd, so this wins.
        services.AddSingleton<IDistributedCache>(l2);
        services.AddHeroCaching(config);

        var provider = services.BuildServiceProvider();
        return new Harness(
            provider.GetRequiredService<HybridCache>(),
            provider.GetRequiredService<GlobalHybridCache>(),
            provider.GetRequiredService<CacheKeyScope>(),
            l2,
            accessor,
            provider);
    }

    #region Keys are partitioned by tenant

    [Fact]
    public async Task GetOrCreateAsync_Should_Give_TwoTenants_TwoEntries_For_TheSameLogicalKey()
    {
        using var h = Build();
        var factoryRuns = 0;

        h.Tenant.TenantId = TenantA;
        var a = await h.Cache.GetOrCreateAsync(
            "theme",
            TenantA,
            (s, ct) => { Interlocked.Increment(ref factoryRuns); return ValueTask.FromResult($"value-for-{s}"); });

        h.Tenant.TenantId = TenantB;
        var b = await h.Cache.GetOrCreateAsync(
            "theme",
            TenantB,
            (s, ct) => { Interlocked.Increment(ref factoryRuns); return ValueTask.FromResult($"value-for-{s}"); });

        a.ShouldBe("value-for-alpha");
        b.ShouldBe("value-for-beta");
        factoryRuns.ShouldBe(2, "the same logical key in two tenants must miss twice, not serve A's value to B.");

        // And back in A the entry is still A's — nothing was overwritten.
        h.Tenant.TenantId = TenantA;
        var again = await h.Cache.GetOrCreateAsync(
            "theme",
            TenantA,
            (s, ct) => { Interlocked.Increment(ref factoryRuns); return ValueTask.FromResult("should-not-run"); });
        again.ShouldBe("value-for-alpha");
        factoryRuns.ShouldBe(2);
    }

    [Fact]
    public async Task SetAsync_Should_Write_The_TenantPrefixed_PhysicalKey()
    {
        using var h = Build();

        h.Tenant.TenantId = TenantA;
        await h.Cache.SetAsync("theme", "a-theme");
        h.Tenant.TenantId = TenantB;
        await h.Cache.SetAsync("theme", "b-theme");

        (await h.L2.GetAsync("t:alpha:theme")).ShouldNotBeNull();
        (await h.L2.GetAsync("t:beta:theme")).ShouldNotBeNull();
        (await h.L2.GetAsync("theme")).ShouldBeNull(
            "nothing may be written under the bare logical key — that is the shared bucket the prefix removes.");
    }

    [Fact]
    public async Task PhysicalKey_Helper_Should_Agree_With_What_TheCache_Writes()
    {
        // The idempotency filter probes L2 with CacheKeyScope.TenantKey(...). If the helper and the
        // decorator ever disagreed, every replay would silently become a fresh execution.
        using var h = Build();
        h.Tenant.TenantId = TenantA;

        await h.Cache.SetAsync("idem:req-1", "response");

        var physical = h.Scope.TenantKey("idem:req-1");
        physical.ShouldBe("t:alpha:idem:req-1");
        (await h.L2.GetAsync(physical)).ShouldNotBeNull();
    }

    [Fact]
    public async Task RemoveAsync_Should_Evict_Only_TheCallingTenants_Entry()
    {
        using var h = Build();

        h.Tenant.TenantId = TenantA;
        await h.Cache.SetAsync("theme", "a-theme");
        h.Tenant.TenantId = TenantB;
        await h.Cache.SetAsync("theme", "b-theme");

        h.Tenant.TenantId = TenantA;
        await h.Cache.RemoveAsync("theme");

        (await ReadOrDefaultAsync(h, "theme")).ShouldBe("miss");
        h.Tenant.TenantId = TenantB;
        (await ReadOrDefaultAsync(h, "theme")).ShouldBe("b-theme");
    }

    [Fact]
    public async Task RemoveAsync_Many_Should_Scope_EveryKey()
    {
        using var h = Build();

        h.Tenant.TenantId = TenantA;
        await h.Cache.SetAsync("k1", "a1");
        await h.Cache.SetAsync("k2", "a2");
        h.Tenant.TenantId = TenantB;
        await h.Cache.SetAsync("k1", "b1");
        await h.Cache.SetAsync("k2", "b2");

        h.Tenant.TenantId = TenantA;
        await h.Cache.RemoveAsync(["k1", "k2"]);

        (await ReadOrDefaultAsync(h, "k1")).ShouldBe("miss");
        (await ReadOrDefaultAsync(h, "k2")).ShouldBe("miss");

        h.Tenant.TenantId = TenantB;
        (await ReadOrDefaultAsync(h, "k1")).ShouldBe("b1");
        (await ReadOrDefaultAsync(h, "k2")).ShouldBe("b2");
    }

    #endregion

    #region Tags are partitioned by tenant

    [Fact]
    public async Task RemoveByTagAsync_Should_Not_Reach_AnotherTenants_Entries()
    {
        // The cheap cross-tenant eviction this issue closes: before #77, RemoveByTagAsync("permissions")
        // from any tenant cleared every tenant's permission entries.
        using var h = Build();

        h.Tenant.TenantId = TenantA;
        await h.Cache.SetAsync("perm:u:1", "a-perms", tags: ["permissions"]);
        h.Tenant.TenantId = TenantB;
        await h.Cache.SetAsync("perm:u:1", "b-perms", tags: ["permissions"]);

        h.Tenant.TenantId = TenantA;
        await h.Cache.RemoveByTagAsync("permissions");

        (await ReadOrDefaultAsync(h, "perm:u:1")).ShouldBe("miss");
        h.Tenant.TenantId = TenantB;
        (await ReadOrDefaultAsync(h, "perm:u:1")).ShouldBe("b-perms",
            "tenant A's invalidation must not evict tenant B — tags are scoped like keys.");
    }

    [Fact]
    public async Task RemoveByTagAsync_Many_Should_Scope_EveryTag()
    {
        using var h = Build();

        h.Tenant.TenantId = TenantA;
        await h.Cache.SetAsync("k1", "a1", tags: ["themes"]);
        await h.Cache.SetAsync("k2", "a2", tags: ["permissions"]);
        h.Tenant.TenantId = TenantB;
        await h.Cache.SetAsync("k1", "b1", tags: ["themes"]);
        await h.Cache.SetAsync("k2", "b2", tags: ["permissions"]);

        h.Tenant.TenantId = TenantA;
        await h.Cache.RemoveByTagAsync(["themes", "permissions"]);

        (await ReadOrDefaultAsync(h, "k1")).ShouldBe("miss");
        (await ReadOrDefaultAsync(h, "k2")).ShouldBe("miss");

        h.Tenant.TenantId = TenantB;
        (await ReadOrDefaultAsync(h, "k1")).ShouldBe("b1");
        (await ReadOrDefaultAsync(h, "k2")).ShouldBe("b2");
    }

    /// <summary>
    /// HybridCache treats the tag <c>"*"</c> as "flush everything" — but scoping rewrites tags like
    /// any other, so tenant A's <c>"*"</c> becomes the physical tag <c>"t:alpha:*"</c>, which no entry
    /// was ever tagged with. The isolation outcome is right (A's wildcard cannot reach B's entries or
    /// the global cache), but it is not a flush-all: it evicts nothing at all, not even A's own
    /// entries. There is no wildcard that survives scoping.
    /// </summary>
    [Fact]
    public async Task RemoveByTagAsync_Wildcard_Should_Evict_Nothing_NotEvenTheCallersOwnEntries()
    {
        using var h = Build();

        h.Tenant.TenantId = TenantA;
        await h.Cache.SetAsync("k1", "a1", tags: ["themes"]);
        await h.Cache.SetAsync("k2", "a2", tags: ["permissions"]);
        h.Tenant.TenantId = TenantB;
        await h.Cache.SetAsync("k1", "b1", tags: ["themes"]);
        h.Tenant.TenantId = null;
        await h.Global.SetAsync("k1", "global-1", tags: ["themes"]);

        h.Tenant.TenantId = TenantA;
        await h.Cache.RemoveByTagAsync("*");

        (await ReadOrDefaultAsync(h, "k1")).ShouldBe("a1",
            "the physical tag \"t:alpha:*\" matches nothing anyone ever set, so even A's own entries survive.");
        (await ReadOrDefaultAsync(h, "k2")).ShouldBe("a2");

        h.Tenant.TenantId = TenantB;
        (await ReadOrDefaultAsync(h, "k1")).ShouldBe("b1", "A's wildcard must not reach B's entries either.");

        h.Tenant.TenantId = null;
        var stillGlobal = await h.Global.GetOrCreateAsync(
            "k1", 0, static (s, ct) => ValueTask.FromResult("should-not-run"));
        stillGlobal.ShouldBe("global-1", "nor the global cache's.");
    }

    #endregion

    #region No tenant throws — never a fallback

    [Fact]
    public async Task Every_Overload_Should_Throw_When_NoTenantIsAmbient()
    {
        using var h = Build();
        h.Tenant.TenantId = null;

        var getOrCreate = await Should.ThrowAsync<InvalidOperationException>(async () =>
            await h.Cache.GetOrCreateAsync("theme", 0, static (s, ct) => ValueTask.FromResult("v")));
        getOrCreate.Message.ShouldContain(
            "theme",
            customMessage: "the message has to name the key, or the author cannot find the call site.");
        getOrCreate.Message.ShouldContain(
            "GlobalHybridCache",
            customMessage: "and it has to name the way out, or the next person invents a fallback tenant.");

        await Should.ThrowAsync<InvalidOperationException>(async () => await h.Cache.SetAsync("theme", "v"));
        await Should.ThrowAsync<InvalidOperationException>(async () => await h.Cache.RemoveAsync("theme"));
        await Should.ThrowAsync<InvalidOperationException>(async () => await h.Cache.RemoveAsync(["theme"]));
        await Should.ThrowAsync<InvalidOperationException>(async () => await h.Cache.RemoveByTagAsync("themes"));
        await Should.ThrowAsync<InvalidOperationException>(async () => await h.Cache.RemoveByTagAsync(["themes"]));
    }

    [Fact]
    public async Task Tags_Should_Throw_When_NoTenantIsAmbient_Even_Though_TheKeyIsFine()
    {
        using var h = Build();
        h.Tenant.TenantId = null;

        var ex = await Should.ThrowAsync<InvalidOperationException>(async () =>
            await h.Cache.SetAsync("theme", "v", tags: ["themes"]));
        ex.Message.ShouldContain("theme");
    }

    [Fact]
    public void A_Blank_TenantId_Should_Count_As_NoTenant()
    {
        // Whitespace is how an "id" arrives from a half-populated context. Treating it as a tenant
        // would create the partition "t: :theme" and share it between every such caller.
        using var h = Build();

        h.Tenant.TenantId = "   ";
        h.Scope.HasTenant.ShouldBeFalse();
        h.Scope.AmbientTenantId.ShouldBeNull();
        Should.Throw<InvalidOperationException>(() => h.Scope.TenantKey("theme"));
    }

    #endregion

    #region The global cache

    [Fact]
    public async Task GlobalCache_Should_Work_With_NoTenant_And_Stay_Disjoint_From_TenantEntries()
    {
        using var h = Build();

        // Written with no tenant at all — this is the impersonation-grant hook's situation.
        h.Tenant.TenantId = null;
        await h.Global.SetAsync("impgrant:jti-1", "revoked");

        (await h.L2.GetAsync("g:impgrant:jti-1")).ShouldNotBeNull();

        // A tenant writing the same logical key gets its own entry, and cannot see the global one.
        h.Tenant.TenantId = TenantA;
        await h.Cache.SetAsync("impgrant:jti-1", "tenant-value");

        (await ReadOrDefaultAsync(h, "impgrant:jti-1")).ShouldBe("tenant-value");

        h.Tenant.TenantId = null;
        var fromGlobal = await h.Global.GetOrCreateAsync(
            "impgrant:jti-1",
            0,
            static (s, ct) => ValueTask.FromResult("should-not-run"));
        fromGlobal.ShouldBe("revoked");
    }

    [Fact]
    public async Task GlobalCache_Invalidation_Should_Not_Touch_TenantEntries()
    {
        using var h = Build();

        h.Tenant.TenantId = TenantA;
        await h.Cache.SetAsync("k", "tenant-value", tags: ["shared-tag"]);

        h.Tenant.TenantId = null;
        await h.Global.SetAsync("k", "global-value", tags: ["shared-tag"]);
        await h.Global.RemoveByTagAsync("shared-tag");

        h.Tenant.TenantId = TenantA;
        (await ReadOrDefaultAsync(h, "k")).ShouldBe("tenant-value",
            "the g: and t: namespaces are disjoint, so a global purge leaves tenant entries alone.");
    }

    [Fact]
    public async Task TenantInvalidation_Should_Not_Touch_GlobalEntries()
    {
        using var h = Build();

        h.Tenant.TenantId = null;
        await h.Global.SetAsync("k", "global-value", tags: ["shared-tag"]);

        h.Tenant.TenantId = TenantA;
        await h.Cache.SetAsync("k", "tenant-value", tags: ["shared-tag"]);
        await h.Cache.RemoveByTagAsync("shared-tag");
        await h.Cache.RemoveAsync("k");

        h.Tenant.TenantId = null;
        var stillThere = await h.Global.GetOrCreateAsync(
            "k", 0, static (s, ct) => ValueTask.FromResult("should-not-run"));
        stillThere.ShouldBe("global-value");
    }

    [Fact]
    public void GlobalKey_Helper_Should_Use_The_GlobalNamespace()
    {
        CacheKeyScope.GlobalKey("impgrant:x").ShouldBe("g:impgrant:x");
        CacheKeyScope.GlobalTag("idempotency").ShouldBe("g:idempotency");
    }

    #endregion

    #region Registration

    [Fact]
    public void Resolving_TheCache_Should_Throw_A_Guiding_Error_When_NoAccessorIsRegistered()
    {
        // A host that composed caching but neither multitenancy nor singleTenant:true has made no
        // decision about tenancy. Fail at the first resolution with the two options spelled out —
        // the one thing we must not do is pick one for them.
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder().Build();
        services.AddSingleton<IConfiguration>(config);
        services.AddHeroCaching(config);

        using var provider = services.BuildServiceProvider();

        var ex = Should.Throw<InvalidOperationException>(() => provider.GetRequiredService<HybridCache>());
        ex.Message.ShouldContain("ICacheTenantAccessor");
        ex.Message.ShouldContain("singleTenant: true");
    }

    [Fact]
    public async Task SingleTenant_Registration_Should_Use_The_Fixed_Partition()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder().Build();
        var l2 = new RecordingDistributedCache();
        services.AddSingleton<IConfiguration>(config);
        services.AddSingleton<IDistributedCache>(l2);
        services.AddHeroCaching(config, singleTenant: true);

        using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<HybridCache>();

        await cache.SetAsync("theme", "v");

        (await l2.GetAsync($"t:{SingleTenantCacheTenantAccessor.SingleTenantId}:theme")).ShouldNotBeNull();
    }

    [Fact]
    public void SingleTenantId_Should_Be_Unreachable_By_A_RealTenant()
    {
        // Tenant ids are validated against ^[a-z0-9][a-z0-9-]{1,62}$ (CreateTenantCommandValidator),
        // so the fixed partition can never be claimed by a tenant someone creates.
        SingleTenantCacheTenantAccessor.SingleTenantId.ShouldBe("_single");
        System.Text.RegularExpressions.Regex
            .IsMatch(SingleTenantCacheTenantAccessor.SingleTenantId, "^[a-z0-9][a-z0-9-]{1,62}$")
            .ShouldBeFalse();
    }

    #endregion

    /// <summary>Reads through the tenant cache, reporting "miss" when the factory had to run.</summary>
    private static async Task<string> ReadOrDefaultAsync(Harness h, string logicalKey)
        => await h.Cache.GetOrCreateAsync(
            logicalKey,
            0,
            static (s, ct) => ValueTask.FromResult("miss"));
}
