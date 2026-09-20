using Boilerplate.BuildingBlocks.Shared.Storage;
using Boilerplate.BuildingBlocks.Storage;
using Boilerplate.BuildingBlocks.Storage.Keys;
using Boilerplate.BuildingBlocks.Storage.Local;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Framework.Tests.Storage;

public sealed class LocalStorageServiceTests : IDisposable
{
    private sealed class Probe { }

    /// <summary>
    /// The owner every upload here belongs to — a user id for an avatar, an asset slot for a brand
    /// asset (#83). It becomes a segment of the key, which is what lets a delete be owner-scoped.
    /// </summary>
    private const string Owner = "owner-1";

    private readonly string _root;
    private readonly AmbientTenantStorageKeys _keys = new("acme");
    private readonly LocalStorageService _sut;

    public LocalStorageServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "boilerplate-local-storage-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        var environment = Substitute.For<IWebHostEnvironment>();
        environment.WebRootPath.Returns(_root);
        environment.ContentRootPath.Returns(_root);

        _sut = new LocalStorageService(environment, _keys, NullLogger<LocalStorageService>.Instance);
    }

    private static FileUploadRequest PngRequest(string fileName = "avatar.png")
        => new()
        {
            FileName = fileName,
            ContentType = "image/png",
            Data = new List<byte> { 1, 2, 3, 4 }
        };

    #region Happy Path

    [Fact]
    public async Task UploadAsync_Should_PersistFileUnderTheTenantsPublicPrefix_When_ValidImage()
    {
        // Arrange
        var request = PngRequest();

        // Act
        var path = await _sut.UploadAsync<Probe>(request, FileType.Image, Owner);

        // Assert — `uploads/` stays outermost (it is what the deploy bucket policy publishes), the
        // tenant is a directory level inside it, and the owner is a level inside that (#83).
        path.ShouldStartWith("uploads/tenants/acme/probe/owner-1/");
        path.ShouldContain("_avatar.png");
        path.ShouldNotContain("\\");
        File.Exists(Path.Combine(_root, path.Replace('/', Path.DirectorySeparatorChar))).ShouldBeTrue();
    }

    [Fact]
    public async Task UploadAsync_Should_ProduceDifferentPaths_When_TwoTenantsUploadTheSameFile()
    {
        var acme = await _sut.UploadAsync<Probe>(PngRequest(), FileType.Image, Owner);

        _keys.Current = "globex";
        var globex = await _sut.UploadAsync<Probe>(PngRequest(), FileType.Image, Owner);

        acme.ShouldStartWith("uploads/tenants/acme/");
        globex.ShouldStartWith("uploads/tenants/globex/");
    }

    [Fact]
    public async Task UploadDownloadExists_Should_RoundTrip_When_FileUploaded()
    {
        // Arrange
        var request = PngRequest();

        // Act
        var path = await _sut.UploadAsync<Probe>(request, FileType.Image, Owner);
        var exists = await _sut.ExistsAsync(path);
        var size = await _sut.GetSizeAsync(path);
        var download = await _sut.DownloadAsync(path);

        // Assert
        exists.ShouldBeTrue();
        size.ShouldBe(4);
        download.ShouldNotBeNull();
        download!.ContentType.ShouldBe("image/png");
        download.ContentLength.ShouldBe(4);
        await download.Stream.DisposeAsync();
    }

    [Fact]
    public async Task DownloadAsync_Should_ServeTheContentTypeDerivedFromTheExtension_NotTheClientHeader()
    {
        // Local never persists the client's Content-Type — download derives it from the file name
        // via FileExtensionContentTypeProvider — so a client claiming "evil.png" is text/html can't
        // get that header served back (#78 hardening item 4; this provider needed no code change,
        // only pinning that it already holds).
        var request = PngRequest();
        request.ContentType = "text/html";

        var path = await _sut.UploadAsync<Probe>(request, FileType.Image, Owner);
        var download = await _sut.DownloadAsync(path);

        download!.ContentType.ShouldBe("image/png");
        await download.Stream.DisposeAsync();
    }

    [Fact]
    public async Task RemoveAsync_Should_DeleteFile_When_FileExists()
    {
        // Arrange
        var path = await _sut.UploadAsync<Probe>(PngRequest(), FileType.Image, Owner);
        var diskPath = path.Replace('/', Path.DirectorySeparatorChar);

        // Act — a backslash-separated form of the same key still resolves (Windows callers).
        await _sut.RemoveAsync(diskPath);

        // Assert
        (await _sut.ExistsAsync(path)).ShouldBeFalse();
    }

    [Fact]
    public async Task RemoveAsync_Should_AcceptThePersistedPublicUrl_When_ReplacingAnAsset()
    {
        // What AppUser.ImageUrl and TenantTheme persist is BuildPublicUrl's output, not the key.
        // Mapping it back is the block's job; a caller must not have to.
        var path = await _sut.UploadAsync<Probe>(PngRequest(), FileType.Image, Owner);
        var url = _sut.BuildPublicUrl(path);

        url.ShouldBe($"/{path}");
        await _sut.RemoveAsync(url);

        (await _sut.ExistsAsync(path)).ShouldBeFalse();
    }

    [Fact]
    public async Task HeadObjectAsync_Should_ReturnMetadata_When_FileExists()
    {
        // Arrange
        var path = await _sut.UploadAsync<Probe>(PngRequest(), FileType.Image, Owner);

        // Act
        var metadata = await _sut.HeadObjectAsync(path);

        // Assert
        metadata.ShouldNotBeNull();
        metadata!.SizeBytes.ShouldBe(4);
        metadata.ContentType.ShouldBe("image/png");
    }

    [Fact]
    public void ComposeKey_Should_PrefixTheAmbientTenant_In_BothSpaces()
    {
        _sut.ComposeKey(StorageSpace.Private, "myfiles/2026/09/ab/x.pdf")
            .ShouldBe("tenants/acme/myfiles/2026/09/ab/x.pdf");
        _sut.ComposeKey(StorageSpace.Public, "probe/x.png")
            .ShouldBe("uploads/tenants/acme/probe/x.png");
    }

    #endregion

    #region Cross-tenant refusal

    [Fact]
    public async Task EveryKeyTakingOperation_Should_Refuse_AnotherTenantsRealKey()
    {
        // Arrange — tenant A stores a file, then tenant B comes along holding its exact key.
        var key = await _sut.UploadAsync<Probe>(PngRequest("secret.png"), FileType.Image, Owner);
        var diskPath = Path.Combine(_root, key.Replace('/', Path.DirectorySeparatorChar));
        _keys.Current = "globex";

        // Act & Assert — every entry point refuses, and none of them touches the disk.
        await Should.ThrowAsync<StorageKeyNotOwnedException>(() => _sut.DownloadAsync(key));
        await Should.ThrowAsync<StorageKeyNotOwnedException>(() => _sut.ExistsAsync(key));
        await Should.ThrowAsync<StorageKeyNotOwnedException>(() => _sut.GetSizeAsync(key));
        await Should.ThrowAsync<StorageKeyNotOwnedException>(() => _sut.RemoveAsync(key));
        await Should.ThrowAsync<StorageKeyNotOwnedException>(() => _sut.HeadObjectAsync(key));
        await Should.ThrowAsync<StorageKeyNotOwnedException>(
            () => _sut.GenerateUploadUrlAsync(key, "image/png", 1024, TimeSpan.FromMinutes(5)));
        await Should.ThrowAsync<StorageKeyNotOwnedException>(
            () => _sut.GenerateDownloadUrlAsync(key, TimeSpan.FromMinutes(5)));
        Should.Throw<StorageKeyNotOwnedException>(() => _sut.BuildPublicUrl(key));

        File.Exists(diskPath).ShouldBeTrue();
    }

    [Theory]
    [InlineData("tenants/globex/myfiles/2026/09/ab/report.pdf")]  // another tenant's key
    [InlineData("uploads/tenants/acme-2/probe/x.png")]            // a tenant id ours is a prefix of
    [InlineData("tenants/acme/../globex/x.png")]                  // traversal
    // One leading slash is the persisted server-relative URL and is mapped back to the key on
    // purpose (see RemoveAsync_Should_AcceptThePersistedPublicUrl…); a second one is not.
    [InlineData("//tenants/acme/x.png")]
    [InlineData("///tenants/acme/x.png")]
    [InlineData("tenants//acme/x.png")]
    [InlineData("tenants/acme/..%2f..%2fglobex/x.png")]           // encoded separators
    [InlineData("Tenants/acme/x.png")]                            // case games
    [InlineData("uploads/probe/legacy.png")]                      // pre-#78 flat key
    [InlineData("")]
    [InlineData("   ")]
    public async Task RemoveAsync_Should_Refuse_AKeyOutsideTheTenantsPrefixes(string key)
    {
        await Should.ThrowAsync<StorageKeyNotOwnedException>(() => _sut.RemoveAsync(key));
    }

    [Fact]
    public async Task RemoveIfOwnedAsync_Should_SkipRatherThanThrow_When_TheHandleIsNotOurs()
    {
        // The "replace my avatar" path: a development database from before #78 still holds flat
        // keys, and the profile endpoint lets a user store any URL at all. Neither is ours to
        // delete, and neither may turn a profile save into a 500.
        (await _sut.RemoveIfOwnedAsync("uploads/probe/legacy_avatar.png")).ShouldBeFalse();
        (await _sut.RemoveIfOwnedAsync("https://cdn.example.com/avatars/me.png")).ShouldBeFalse();
        (await _sut.RemoveIfOwnedAsync("tenants/globex/probe/theirs.png")).ShouldBeFalse();
        (await _sut.RemoveIfOwnedAsync(null)).ShouldBeFalse();
    }

    [Fact]
    public async Task RemoveIfOwnedAsync_Should_Delete_When_TheHandleIsOurs()
    {
        var key = await _sut.UploadAsync<Probe>(PngRequest(), FileType.Image, Owner);

        (await _sut.RemoveIfOwnedAsync(_sut.BuildPublicUrl(key))).ShouldBeTrue();

        (await _sut.ExistsAsync(key)).ShouldBeFalse();
    }

    #endregion

    #region Owner scoping inside one tenant (#83)

    [Fact]
    public async Task UploadAsync_Should_GiveTwoOwnersSeparatePrefixes_When_TheyUploadTheSameFile()
    {
        var mine = await _sut.UploadAsync<Probe>(PngRequest(), FileType.Image, "user-a");
        var theirs = await _sut.UploadAsync<Probe>(PngRequest(), FileType.Image, "user-b");

        mine.ShouldStartWith("uploads/tenants/acme/probe/user-a/");
        theirs.ShouldStartWith("uploads/tenants/acme/probe/user-b/");
    }

    [Fact]
    public async Task RemoveIfOwnedAsyncOfT_Should_Refuse_AnotherOwnersObject_InTheSameTenant()
    {
        // The hole #83 closes. Tenant ownership says yes to this key — it is this tenant's — so the
        // tenant-wide overload would delete it. Inside one tenant that is the difference between
        // "replace my avatar" and "delete the avatar of whoever I named".
        var theirs = await _sut.UploadAsync<Probe>(PngRequest("theirs.png"), FileType.Image, "user-b");

        (await _sut.RemoveIfOwnedAsync<Probe>(theirs, "user-a")).ShouldBeFalse();

        (await _sut.ExistsAsync(theirs)).ShouldBeTrue("another owner's bytes must survive");
        (await _sut.RemoveIfOwnedAsync(theirs)).ShouldBeTrue("…while the tenant-wide overload would have deleted them");
    }

    [Fact]
    public async Task RemoveIfOwnedAsyncOfT_Should_Delete_TheOwnersOwnObject_ByKeyOrByPersistedUrl()
    {
        var byKey = await _sut.UploadAsync<Probe>(PngRequest(), FileType.Image, Owner);
        (await _sut.RemoveIfOwnedAsync<Probe>(byKey, Owner)).ShouldBeTrue();
        (await _sut.ExistsAsync(byKey)).ShouldBeFalse();

        var byUrl = await _sut.UploadAsync<Probe>(PngRequest(), FileType.Image, Owner);
        (await _sut.RemoveIfOwnedAsync<Probe>(_sut.BuildPublicUrl(byUrl), Owner)).ShouldBeTrue();
        (await _sut.ExistsAsync(byUrl)).ShouldBeFalse();
    }

    [Fact]
    public async Task RemoveIfOwnedAsyncOfT_Should_Skip_TheValuesAnExistingRowMayHold()
    {
        // No migration backfills these (#83): the first replace or remove leaves them alone and the
        // column is then overwritten with a server-issued value.
        (await _sut.RemoveIfOwnedAsync<Probe>("https://cdn.example.com/avatars/me.png", Owner)).ShouldBeFalse();
        (await _sut.RemoveIfOwnedAsync<Probe>("uploads/probe/legacy_avatar.png", Owner)).ShouldBeFalse();
        (await _sut.RemoveIfOwnedAsync<Probe>("uploads/tenants/acme/probe/pre-owner.png", Owner)).ShouldBeFalse();
        (await _sut.RemoveIfOwnedAsync<Probe>("tenants/globex/probe/theirs.png", Owner)).ShouldBeFalse();
        (await _sut.RemoveIfOwnedAsync<Probe>(null, Owner)).ShouldBeFalse();
    }

    [Fact]
    public async Task RemoveIfOwnedAsyncOfT_Should_Refuse_AnOwnerSegmentThatTriesToWidenTheMatch()
    {
        // A separator inside the owner would make the prefix match more than the owner's own
        // objects; the key grammar has no character to express one, so it is sanitized away.
        var key = await _sut.UploadAsync<Probe>(PngRequest(), FileType.Image, "user-a");

        (await _sut.RemoveIfOwnedAsync<Probe>(key, "user-a/../user-b")).ShouldBeFalse();
        (await _sut.ExistsAsync(key)).ShouldBeTrue();
    }

    #endregion

    #region Cross-tenant refusal, continued

    [Fact]
    public async Task EveryOperation_Should_Throw_When_ThereIsNoAmbientTenant()
    {
        // No fallback key space, ever: work with no tenant has to enter one through ITenantScope.
        _keys.Current = null;

        await Should.ThrowAsync<MissingStorageTenantException>(
            () => _sut.UploadAsync<Probe>(PngRequest(), FileType.Image, Owner));
        await Should.ThrowAsync<MissingStorageTenantException>(() => _sut.ExistsAsync("tenants/acme/x.png"));
        await Should.ThrowAsync<MissingStorageTenantException>(() => _sut.RemoveAsync("tenants/acme/x.png"));
        await Should.ThrowAsync<MissingStorageTenantException>(() => _sut.RemoveIfOwnedAsync("tenants/acme/x.png"));
        await Should.ThrowAsync<MissingStorageTenantException>(
            () => _sut.RemoveIfOwnedAsync<Probe>("uploads/tenants/acme/probe/owner-1/x.png", Owner));
        Should.Throw<MissingStorageTenantException>(() => _sut.ComposeKey(StorageSpace.Private, "x.png"));
    }

    #endregion

    #region URL generation

    [Fact]
    public async Task GenerateUploadUrlAsync_Should_ReturnLocalTokenUrl_When_KeyProvided()
    {
        // Act
        var result = await _sut.GenerateUploadUrlAsync(
            "uploads/tenants/acme/probe/file.png", "image/png", 1024, TimeSpan.FromMinutes(5));

        // Assert
        result.Url.Scheme.ShouldBe("local");
        result.RequiredHeaders["Content-Type"].ShouldBe("image/png");
        result.ExpiresAt.ShouldBeGreaterThan(DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task GenerateUploadUrlAsync_Should_MintATokenNoOtherTenantCanRedeem()
    {
        // The token is a bearer string, so what confines it is the key it carries.
        var result = await _sut.GenerateUploadUrlAsync(
            "uploads/tenants/acme/probe/file.png", "image/png", 1024, TimeSpan.FromMinutes(5));
        var token = result.Url.AbsoluteUri["local://upload/".Length..];

        LocalStorageService.SharedTokenStore.Consume(token, "globex").ShouldBeNull();
    }

    [Fact]
    public async Task GenerateDownloadUrlAsync_Should_ReturnRelativeUrl_When_KeyProvided()
    {
        // Act — the persisted server-relative form (one leading slash) maps back to the key.
        var uri = await _sut.GenerateDownloadUrlAsync("/uploads/tenants/acme/probe/file.png", TimeSpan.FromMinutes(5));

        // Assert
        uri.IsAbsoluteUri.ShouldBeFalse();
        uri.OriginalString.ShouldBe("/uploads/tenants/acme/probe/file.png");
    }

    [Fact]
    public async Task GenerateDownloadUrlAsync_Should_ReturnTheSameServerRelativePath_For_Public_And_Private_FilesModuleKeys()
    {
        // Local storage is the dev fallback: it has no signing and serves everything it stores from
        // wwwroot through UseStaticFiles, so the URL itself is the capability — the unguessable
        // FileAsset id in the key is all that separates one object from another.
        //
        // The visibility gate therefore lives one level up, in the Files module
        // (PublicFileUrlFactory never mints a URL for a Private asset), not here. This test pins that
        // the provider treats both alike so nobody mistakes local behaviour for enforcement. What it
        // does *not* treat alike is another tenant's key — see the refusal tests above.
        const string key = "tenants/acme/myfiles/2026/05/0f7b/secret.pdf";

        var publicUrl = await _sut.GenerateDownloadUrlAsync(key, TimeSpan.FromMinutes(5), "inline; filename=\"secret.pdf\"");
        var privateUrl = await _sut.GenerateDownloadUrlAsync(key, TimeSpan.FromMinutes(5));

        publicUrl.IsAbsoluteUri.ShouldBeFalse();
        publicUrl.OriginalString.ShouldBe($"/{key}");
        privateUrl.OriginalString.ShouldBe(publicUrl.OriginalString);
    }

    [Fact]
    public void BuildPublicUrl_Should_NormalizeToServerRelativePath_When_KeyHasBackslashes()
    {
        // Act
        var url = _sut.BuildPublicUrl("uploads\\tenants\\acme\\probe\\file.png");

        // Assert
        url.ShouldBe("/uploads/tenants/acme/probe/file.png");
    }

    #endregion

    #region Exception / Edge Cases

    [Fact]
    public async Task UploadAsync_Should_Throw_When_ExtensionNotAllowed()
    {
        // Arrange
        var request = PngRequest("malware.exe");

        // Act & Assert
        await Should.ThrowAsync<InvalidOperationException>(() =>
            _sut.UploadAsync<Probe>(request, FileType.Image, Owner));
    }

    [Fact]
    public async Task UploadAsync_Should_Throw_When_FileExceedsMaxSize()
    {
        // Arrange — Image limit is 5 MB; build a 6 MB payload.
        var request = new FileUploadRequest
        {
            FileName = "big.png",
            ContentType = "image/png",
            Data = new List<byte>(new byte[6 * 1024 * 1024])
        };

        // Act & Assert
        await Should.ThrowAsync<InvalidOperationException>(() =>
            _sut.UploadAsync<Probe>(request, FileType.Image, Owner));
    }

    [Fact]
    public async Task ReadOperations_Should_Refuse_When_PathBlank()
    {
        // A blank handle names no object this tenant owns, so it gets the same refusal a foreign
        // key gets rather than a quiet "absent" that hides a caller bug.
        await Should.ThrowAsync<StorageKeyNotOwnedException>(() => _sut.ExistsAsync(" "));
        await Should.ThrowAsync<StorageKeyNotOwnedException>(() => _sut.GetSizeAsync(" "));
        await Should.ThrowAsync<StorageKeyNotOwnedException>(() => _sut.DownloadAsync(" "));
        await Should.ThrowAsync<StorageKeyNotOwnedException>(() => _sut.HeadObjectAsync(" "));
    }

    [Fact]
    public async Task ReadOperations_Should_ReturnAbsent_When_OwnedKeyIsMissing()
    {
        (await _sut.DownloadAsync("uploads/tenants/acme/probe/missing.png")).ShouldBeNull();
        (await _sut.ExistsAsync("uploads/tenants/acme/probe/missing.png")).ShouldBeFalse();
        (await _sut.GetSizeAsync("uploads/tenants/acme/probe/missing.png")).ShouldBe(0);
        (await _sut.HeadObjectAsync("uploads/tenants/acme/probe/missing.png")).ShouldBeNull();
    }

    [Fact]
    public async Task RemoveAsync_Should_NotThrow_When_OwnedKeyIsMissing()
    {
        await Should.NotThrowAsync(() => _sut.RemoveAsync("uploads/tenants/acme/probe/missing.png"));
    }

    #endregion

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // best-effort temp cleanup
        }
    }
}
