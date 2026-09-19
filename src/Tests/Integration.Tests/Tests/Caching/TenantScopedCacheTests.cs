using Boilerplate.BuildingBlocks.Caching;
using Boilerplate.BuildingBlocks.Shared.Multitenancy;
using Boilerplate.Modules.Multitenancy.Contracts;
using Integration.Tests.Infrastructure;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;

namespace Integration.Tests.Tests.Caching;

/// <summary>
/// The tenant prefix, proved on the real host rather than on a hand-built container: the same
/// <see cref="HybridCache"/> singleton, the same Finbuckle-backed <see cref="ICacheTenantAccessor"/>,
/// and two tenants that were created the way tenants are created.
///
/// Tenants are entered through <see cref="ITenantScope"/>, which is the one mechanism for crossing
/// into a tenant outside a request (ADR-0002) and, since #77, also the one way to reach another
/// tenant's cache entries. There is deliberately no <c>ForTenant(id)</c> escape hatch on the block:
/// an API that lets a caller name someone else's partition is the hole the prefix exists to close.
/// </summary>
[Collection(AppCollectionDefinition.Name)]
public sealed class TenantScopedCacheTests : IAsyncLifetime
{
    private readonly AppWebApplicationFactory _factory;
    private readonly TenantFixtures _tenants;

    private string _otherTenant = default!;

    public TenantScopedCacheTests(AppWebApplicationFactory factory)
    {
        _factory = factory;
        _tenants = new TenantFixtures(factory);
    }

    public async Task InitializeAsync()
    {
        (_otherTenant, _) = await _tenants.CreateProvisionedTenantAsync("cachescope");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private HybridCache Cache => _factory.Services.GetRequiredService<HybridCache>();

    private GlobalHybridCache GlobalCache => _factory.Services.GetRequiredService<GlobalHybridCache>();

    private ITenantScope TenantScope => _factory.Services.GetRequiredService<ITenantScope>();

    /// <summary>Runs <paramref name="work"/> with <paramref name="tenantId"/> installed as ambient.</summary>
    private Task<T> InTenantAsync<T>(string tenantId, Func<CancellationToken, Task<T>> work)
        => TenantScope.RunAsync(tenantId, (_, ct) => work(ct));

    [Fact]
    public async Task TwoTenants_Should_Not_See_EachOthers_Entry_For_TheSameLogicalKey()
    {
        var key = $"probe:{Guid.NewGuid():N}";

        var rootValue = await InTenantAsync(TestConstants.RootTenantId, ct => ReadOrWriteAsync(key, "root-value", ct));
        var otherValue = await InTenantAsync(_otherTenant, ct => ReadOrWriteAsync(key, "other-value", ct));

        rootValue.ShouldBe("root-value");
        otherValue.ShouldBe("other-value",
            "the second tenant must miss and run its own factory, not be served the first tenant's value.");

        // Both entries survive independently.
        (await InTenantAsync(TestConstants.RootTenantId, ct => ReadOrWriteAsync(key, "should-not-run", ct)))
            .ShouldBe("root-value");
        (await InTenantAsync(_otherTenant, ct => ReadOrWriteAsync(key, "should-not-run", ct)))
            .ShouldBe("other-value");
    }

    [Fact]
    public async Task TagInvalidation_In_OneTenant_Should_Leave_TheOthers_Entry_Cached()
    {
        // Before #77, RemoveByTagAsync("permissions") from any tenant cleared every tenant's entries —
        // a one-request, cross-tenant cache flush available to anyone who could change a role.
        var key = $"probe:{Guid.NewGuid():N}";
        var tag = $"probe-tag:{Guid.NewGuid():N}";

        await InTenantAsync(TestConstants.RootTenantId, ct => WriteAsync(key, "root-value", tag, ct));
        await InTenantAsync(_otherTenant, ct => WriteAsync(key, "other-value", tag, ct));

        await InTenantAsync(TestConstants.RootTenantId, async ct =>
        {
            await Cache.RemoveByTagAsync(tag, ct);
            return true;
        });

        (await InTenantAsync(TestConstants.RootTenantId, ct => ReadOrWriteAsync(key, "reloaded", ct)))
            .ShouldBe("reloaded", "the invalidating tenant's own entry must be gone.");
        (await InTenantAsync(_otherTenant, ct => ReadOrWriteAsync(key, "should-not-run", ct)))
            .ShouldBe("other-value", "and the other tenant's must not be.");
    }

    [Fact]
    public async Task KeyInvalidation_In_OneTenant_Should_Leave_TheOthers_Entry_Cached()
    {
        var key = $"probe:{Guid.NewGuid():N}";

        await InTenantAsync(TestConstants.RootTenantId, ct => WriteAsync(key, "root-value", tag: null, ct));
        await InTenantAsync(_otherTenant, ct => WriteAsync(key, "other-value", tag: null, ct));

        await InTenantAsync(TestConstants.RootTenantId, async ct =>
        {
            await Cache.RemoveAsync(key, ct);
            return true;
        });

        (await InTenantAsync(TestConstants.RootTenantId, ct => ReadOrWriteAsync(key, "reloaded", ct)))
            .ShouldBe("reloaded");
        (await InTenantAsync(_otherTenant, ct => ReadOrWriteAsync(key, "should-not-run", ct)))
            .ShouldBe("other-value");
    }

    [Fact]
    public async Task Using_TheTenantCache_With_No_AmbientTenant_Should_Throw()
    {
        // The test method itself runs outside any tenant scope — exactly the situation a hosted
        // service or an authentication event is in.
        var ex = await Should.ThrowAsync<InvalidOperationException>(async () =>
            await Cache.SetAsync($"probe:{Guid.NewGuid():N}", "value"));

        ex.Message.ShouldContain("GlobalHybridCache");
    }

    [Fact]
    public async Task TheGlobalCache_Should_Work_With_No_AmbientTenant_And_Be_Invisible_To_Tenants()
    {
        var key = $"probe:{Guid.NewGuid():N}";

        await GlobalCache.SetAsync(key, "global-value");

        var fromGlobal = await GlobalCache.GetOrCreateAsync(
            key, 0, static (s, ct) => ValueTask.FromResult("should-not-run"));
        fromGlobal.ShouldBe("global-value");

        // A tenant asking for the same logical key sees nothing of it.
        (await InTenantAsync(TestConstants.RootTenantId, ct => ReadOrWriteAsync(key, "tenant-value", ct)))
            .ShouldBe("tenant-value");

        // And writing it in a tenant did not disturb the global entry.
        (await GlobalCache.GetOrCreateAsync(key, 0, static (s, ct) => ValueTask.FromResult("should-not-run")))
            .ShouldBe("global-value");
    }

    [Fact]
    public async Task ThemeService_Should_Cache_Each_Tenants_Theme_Separately()
    {
        // The real call site, through the real service: TenantThemeService no longer puts the tenant
        // in the key, so if the prefix were missing both tenants would share one theme entry.
        var rootTheme = await InTenantAsync(TestConstants.RootTenantId, async ct =>
        {
            using var scope = _factory.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<ITenantThemeService>()
                .GetThemeAsync(TestConstants.RootTenantId, ct);
        });

        var otherTheme = await InTenantAsync(_otherTenant, async ct =>
        {
            using var scope = _factory.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<ITenantThemeService>()
                .GetThemeAsync(_otherTenant, ct);
        });

        rootTheme.ShouldNotBeNull();
        otherTheme.ShouldNotBeNull();

        // Both entries now exist under their own physical key, and neither read threw.
        var scopeHelper = _factory.Services.GetRequiredService<CacheKeyScope>();
        await InTenantAsync(TestConstants.RootTenantId, ct =>
        {
            scopeHelper.TenantKey(CacheKeys.TenantTheme).ShouldBe($"t:{TestConstants.RootTenantId}:theme");
            return Task.FromResult(true);
        });
        await InTenantAsync(_otherTenant, ct =>
        {
            scopeHelper.TenantKey(CacheKeys.TenantTheme).ShouldBe($"t:{_otherTenant}:theme");
            return Task.FromResult(true);
        });
    }

    [Fact]
    public async Task ThemeService_Should_Refuse_To_Touch_AnotherTenants_Theme()
    {
        // One mechanism for crossing tenants, and it is not an argument: entering the tenant.
        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await InTenantAsync(TestConstants.RootTenantId, async ct =>
            {
                using var scope = _factory.Services.CreateScope();
                return await scope.ServiceProvider.GetRequiredService<ITenantThemeService>()
                    .GetThemeAsync(_otherTenant, ct);
            }));
    }

    // ─── helpers ─────────────────────────────────────────────────────

    /// <summary>Reads through the cache, writing <paramref name="onMiss"/> if the entry is absent.</summary>
    private Task<string> ReadOrWriteAsync(string key, string onMiss, CancellationToken ct)
        => Cache.GetOrCreateAsync(key, onMiss, static (s, _) => ValueTask.FromResult(s), cancellationToken: ct).AsTask();

    private async Task<bool> WriteAsync(string key, string value, string? tag, CancellationToken ct)
    {
        await Cache.SetAsync(key, value, tags: tag is null ? null : [tag], cancellationToken: ct);
        return true;
    }
}
