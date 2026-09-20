using Boilerplate.BuildingBlocks.Caching;

namespace Caching.Tests;

/// <summary>
/// Verifies the cache key and tag conventions stay stable — keys are persisted to Redis,
/// so unintentional format changes would silently invalidate every running instance's entries.
/// Every name here is <b>logical</b>: the tenant prefix is the cache's job, not the caller's.
/// </summary>
public sealed class CacheKeysTests
{
    [Fact]
    public void UserPermissions_Should_UseStablePrefix()
    {
        CacheKeys.UserPermissions("abc-123").ShouldBe("perm:u:abc-123");
    }

    [Fact]
    public void TenantTheme_Should_Be_A_TenantLess_Logical_Key()
    {
        // No tenant id in the key: TenantScopedHybridCache supplies the ambient tenant.
        CacheKeys.TenantTheme.ShouldBe("theme");
    }

    [Fact]
    public void DefaultTheme_Should_BeConstant()
    {
        CacheKeys.DefaultTheme.ShouldBe("theme:default");
    }

    [Fact]
    public void IdempotencyEntry_Should_Carry_TheCallerBinding_Then_TheClientKey()
    {
        // The binding (subject + method + path, hashed by the filter) comes first and the
        // client-chosen key last: nothing follows the untrusted part, so nothing in it can be
        // arranged to name another caller's partition.
        CacheKeys.IdempotencyEntry("ab12", "req-42").ShouldBe("idem:ab12:req-42");
    }

    [Fact]
    public void ImpersonationGrantStatus_Should_IndexByJti()
    {
        CacheKeys.ImpersonationGrantStatus("jti-9").ShouldBe("impgrant:jti-9");
    }

    [Fact]
    public void Tags_User_Should_UseUserPrefix()
    {
        CacheKeys.Tags.User("u1").ShouldBe("user:u1");
    }

    [Fact]
    public void Tags_Constants_Should_BeStable()
    {
        CacheKeys.Tags.Permissions.ShouldBe("permissions");
        CacheKeys.Tags.Themes.ShouldBe("themes");
    }
}
