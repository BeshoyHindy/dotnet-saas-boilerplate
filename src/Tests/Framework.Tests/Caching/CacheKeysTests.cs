using Boilerplate.BuildingBlocks.Caching;

namespace Framework.Tests.Caching;

public sealed class CacheKeysTests
{
    #region Keys

    [Fact]
    public void UserPermissions_Should_ScopeByUserId_When_Built()
    {
        CacheKeys.UserPermissions("u-123").ShouldBe("perm:u:u-123");
    }

    [Fact]
    public void TenantTheme_Should_NotCarryATenantId()
    {
        // The tenant prefix is applied by the cache (CacheKeyScope), not composed by the caller.
        CacheKeys.TenantTheme.ShouldBe("theme");
    }

    [Fact]
    public void DefaultTheme_Should_BeStableConstant()
    {
        CacheKeys.DefaultTheme.ShouldBe("theme:default");
    }

    [Fact]
    public void IdempotencyEntry_Should_ScopeByCallerBinding_Then_ClientKey()
    {
        // One shape for both the tenant-scoped and the global entry: the branch is which namespace
        // the filter scopes it into, not a different key.
        CacheKeys.IdempotencyEntry("ff00", "abc").ShouldBe("idem:ff00:abc");
    }

    [Fact]
    public void ImpersonationGrantStatus_Should_IndexByJti_When_Built()
    {
        CacheKeys.ImpersonationGrantStatus("jti-9").ShouldBe("impgrant:jti-9");
    }

    #endregion

    #region Tags

    [Fact]
    public void Tags_Should_ExposeStableConstants()
    {
        CacheKeys.Tags.Permissions.ShouldBe("permissions");
        CacheKeys.Tags.Themes.ShouldBe("themes");
    }

    [Fact]
    public void Tags_Should_ScopeUser_When_Built()
    {
        CacheKeys.Tags.User("u-1").ShouldBe("user:u-1");
    }

    #endregion
}
