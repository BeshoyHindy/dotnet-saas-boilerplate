using Boilerplate.BuildingBlocks.Storage.Keys;

namespace Framework.Tests.Storage;

/// <summary>
/// The composition and ownership algorithm behind every key the Storage block will accept
/// (ADR-0002: "storage object keys are tenant-prefixed by the building block, not by caller
/// convention"). These tests are the specification of the two key spaces and of every way a
/// caller might try to name an object outside its own tenant.
/// </summary>
public sealed class TenantStorageKeyRulesTests
{
    #region Composition

    [Fact]
    public void Compose_Should_PrefixThePrivateSpace_When_SpaceIsPrivate()
    {
        TenantStorageKeyRules.Compose("acme", StorageSpace.Private, "myfiles/2026/09/ab/report.pdf")
            .ShouldBe("tenants/acme/myfiles/2026/09/ab/report.pdf");
    }

    [Fact]
    public void Compose_Should_PrefixThePublicSpace_Under_Uploads_When_SpaceIsPublic()
    {
        // `uploads/` stays the outermost segment because it is the one prefix the deploy stacks
        // grant anonymous read on (deploy/dokploy/tests/compose-contract.test.sh).
        TenantStorageKeyRules.Compose("acme", StorageSpace.Public, "appuser/abc_avatar.png")
            .ShouldBe("uploads/tenants/acme/appuser/abc_avatar.png");
    }

    [Fact]
    public void Compose_Should_ProduceDifferentKeys_When_TwoTenantsUseTheSameRelativePath()
    {
        const string relative = "tenanttheme/deadbeef_logo.png";

        var a = TenantStorageKeyRules.Compose("acme", StorageSpace.Public, relative);
        var b = TenantStorageKeyRules.Compose("globex", StorageSpace.Public, relative);

        a.ShouldNotBe(b);
        a.ShouldBe("uploads/tenants/acme/tenanttheme/deadbeef_logo.png");
        b.ShouldBe("uploads/tenants/globex/tenanttheme/deadbeef_logo.png");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/leading")]                 // absolute
    [InlineData("trailing/")]                // empty last segment
    [InlineData("double//slash")]
    [InlineData("../escape")]
    [InlineData("nested/../escape")]
    [InlineData("./same")]
    [InlineData("encoded%2fseparator")]
    [InlineData("back\\slash")]
    [InlineData("space in segment")]
    public void Compose_Should_Throw_When_RelativePathIsNotWellFormed(string relativePath)
    {
        Should.Throw<ArgumentException>(
            () => TenantStorageKeyRules.Compose("acme", StorageSpace.Private, relativePath));
    }

    [Theory]
    [InlineData("")]
    [InlineData("A")]                        // upper case
    [InlineData("-leading-hyphen")]
    [InlineData("x")]                        // one character — below the 2-char floor
    [InlineData("has_underscore")]
    [InlineData("has/slash")]
    [InlineData("has.dot")]
    public void Compose_Should_Throw_When_TenantIdIsNotASlug(string tenantId)
    {
        // Tenant ids are `^[a-z0-9][a-z0-9-]{1,62}$` (CreateTenantCommandValidator.IdPattern). Anything
        // else would put a character into the key that the segment grammar does not round-trip.
        Should.Throw<ArgumentException>(
            () => TenantStorageKeyRules.Compose(tenantId, StorageSpace.Private, "x/y.png"));
    }

    [Fact]
    public void KeyRootSegments_Should_BeTheFirstSegmentOfEachRoot()
    {
        // The one literal an S3 bucket name or Storage:S3:Prefix's first segment must never collide
        // with — see S3StorageOptions validation (#78 hardening item 2).
        TenantStorageKeyRules.KeyRootSegments.ShouldBe(["tenants", "uploads"]);
    }

    #endregion

    #region Ownership

    [Theory]
    [InlineData("tenants/acme/myfiles/2026/09/ab/report.pdf")]
    [InlineData("uploads/tenants/acme/appuser/abc_avatar.png")]
    public void TryAuthorize_Should_Accept_KeysInEitherSpace_When_TenantMatches(string key)
    {
        TenantStorageKeyRules.TryAuthorize("acme", key, out var authorized).ShouldBeTrue();
        authorized.ShouldBe(key);
    }

    [Fact]
    public void TryAuthorize_Should_Refuse_AnotherTenantsRealKey()
    {
        TenantStorageKeyRules
            .TryAuthorize("acme", "tenants/globex/myfiles/2026/09/ab/report.pdf", out _)
            .ShouldBeFalse();
    }

    [Theory]
    [InlineData("tenants/acme-2/x.png")]
    [InlineData("uploads/tenants/acme-2/x.png")]
    [InlineData("tenants/acmeextra/x.png")]
    public void TryAuthorize_Should_Refuse_ATenantIdThatMerelyStartsWithOurs(string key)
    {
        // The comparison is on the full segment including its trailing '/', so `acme` never
        // matches `acme-2`.
        TenantStorageKeyRules.TryAuthorize("acme", key, out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData("tenants/acme/../globex/x.png")]
    [InlineData("tenants/acme/a/../../globex/x.png")]
    [InlineData("tenants/acme/./x.png")]
    [InlineData("/tenants/acme/x.png")]            // leading slash
    [InlineData("//tenants/acme/x.png")]
    [InlineData("tenants//acme/x.png")]
    [InlineData("tenants/acme//x.png")]
    [InlineData("tenants/acme/x.png/")]            // trailing slash
    [InlineData("tenants/acme/..%2f..%2fglobex/x.png")]
    [InlineData("tenants/acme/%2e%2e/globex/x.png")]
    [InlineData("uploads%2ftenants%2facme%2fx.png")]
    [InlineData("tenants/acme/x\\..\\..\\globex.png")]
    [InlineData("Tenants/acme/x.png")]             // case games on the root
    [InlineData("TENANTS/ACME/x.png")]
    [InlineData("Uploads/tenants/acme/x.png")]
    [InlineData("uploads/Tenants/acme/x.png")]
    [InlineData("uploads/appuser/legacy_avatar.png")]   // pre-#78 flat key: no tenant segment at all
    [InlineData("tenants/acme")]                        // the prefix itself, no object
    [InlineData("tenants/acme/")]
    [InlineData("uploads/tenants/acme/")]
    [InlineData("random/other/place.png")]
    [InlineData("")]
    [InlineData("   ")]
    public void TryAuthorize_Should_Refuse(string key)
    {
        TenantStorageKeyRules.TryAuthorize("acme", key, out var authorized).ShouldBeFalse();
        authorized.ShouldBeEmpty();
    }

    [Fact]
    public void TryAuthorize_Should_Refuse_When_TheAmbientTenantIdIsNotASlug()
    {
        // Defence in depth: a malformed ambient tenant must never be able to name a prefix.
        TenantStorageKeyRules.TryAuthorize("ACME", "tenants/ACME/x.png", out _).ShouldBeFalse();
        TenantStorageKeyRules.TryAuthorize("", "tenants//x.png", out _).ShouldBeFalse();
    }

    [Fact]
    public void TryAuthorize_Should_Refuse_When_TheKeyExceedsTheObjectKeyLimit()
    {
        var key = "tenants/acme/" + new string('a', TenantStorageKeyRules.MaxKeyLength);

        TenantStorageKeyRules.TryAuthorize("acme", key, out _).ShouldBeFalse();
    }

    #endregion
}
