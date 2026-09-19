using Boilerplate.Modules.Files.Services;
using Shouldly;

namespace Files.Tests.Services;

/// <summary>
/// What the Files module builds is now only the tenant-<i>relative</i> part of a key: the tenant
/// prefix is the Storage block's, composed by <c>IStorageService.ComposeKey</c> (ADR-0002, #78).
/// So there is no tenant id to pass and no <c>tenants/</c> literal to assert.
/// </summary>
public class StorageKeyBuilderTests
{
    [Fact]
    public void Build_Should_ProduceCanonicalShape()
    {
        var now = new DateTimeOffset(2026, 5, 12, 0, 0, 0, TimeSpan.Zero);
        var id = Guid.Parse("11111111-2222-3333-4444-555555555555");

        var relativePath = StorageKeyBuilder.Build("Document", id, "holiday photo.png", now);

        relativePath.ShouldBe("document/2026/05/11111111222233334444555555555555/holiday_photo.png");
    }

    [Fact]
    public void Build_Should_NotPrefixTheTenant()
    {
        // The block owns the prefix. A relative path that carried one would be composed into
        // `tenants/{tenant}/tenants/…` — visibly wrong, and this is what keeps it visible.
        var relativePath = StorageKeyBuilder.Build("Document", Guid.NewGuid(), "x.pdf", DateTimeOffset.UtcNow);

        relativePath.ShouldNotStartWith("/");
        relativePath.ShouldNotContain("tenants");
    }

    [Fact]
    public void Build_Should_LowercaseOwnerType()
    {
        var relativePath = StorageKeyBuilder.Build("Document", Guid.NewGuid(), "x.pdf", DateTimeOffset.UtcNow);
        relativePath.ShouldStartWith("document/");
    }

    [Fact]
    public void Build_Should_SanitizeOwnerType()
    {
        // OwnerType reaches here from the request body, so it is sanitized like the file name —
        // a '/' in it would otherwise invent a path segment the block never agreed to.
        var relativePath = StorageKeyBuilder.Build("my/../files", Guid.NewGuid(), "x.pdf", DateTimeOffset.UtcNow);

        relativePath.ShouldStartWith("my_.._files/");
    }

    [Fact]
    public void Sanitize_Should_StripUnsafeCharacters()
    {
        StorageKeyBuilder.Sanitize("ke!llo$.png").ShouldBe("ke_llo_.png");
    }

    [Fact]
    public void Sanitize_Should_PreserveSafeCharacters()
    {
        StorageKeyBuilder.Sanitize("a-b_c.1.png").ShouldBe("a-b_c.1.png");
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Build_Should_RejectEmptyFileName(string fileName)
    {
        Should.Throw<ArgumentException>(
            () => StorageKeyBuilder.Build("o", Guid.NewGuid(), fileName, DateTimeOffset.UtcNow));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Build_Should_RejectEmptyOwnerType(string ownerType)
    {
        Should.Throw<ArgumentException>(
            () => StorageKeyBuilder.Build(ownerType, Guid.NewGuid(), "x.png", DateTimeOffset.UtcNow));
    }
}
