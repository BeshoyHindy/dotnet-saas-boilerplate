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
    public void IdempotencyEntry_Should_ScopeByKeyAlone_When_Built()
    {
        CacheKeys.IdempotencyEntry("abc").ShouldBe("idem:abc");
    }

    [Fact]
    public void GlobalIdempotencyEntry_Should_PartitionBySubjectOrAnonymous()
    {
        CacheKeys.GlobalIdempotencyEntry("u-1", "abc").ShouldBe("idem:s:u-1:abc");
        CacheKeys.GlobalIdempotencyEntry(null, "abc").ShouldBe("idem:anon:abc");
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
        CacheKeys.Tags.Idempotency.ShouldBe("idempotency");
    }

    [Fact]
    public void Tags_Should_ScopeUser_When_Built()
    {
        CacheKeys.Tags.User("u-1").ShouldBe("user:u-1");
    }

    #endregion
}
