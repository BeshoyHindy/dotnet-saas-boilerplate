using Amazon.S3;
using Amazon.S3.Model;
using Boilerplate.BuildingBlocks.Shared.Storage;
using Boilerplate.BuildingBlocks.Storage;
using Boilerplate.BuildingBlocks.Storage.Keys;
using Boilerplate.BuildingBlocks.Storage.S3;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Framework.Tests.Storage;

/// <summary>
/// The S3 provider against a substituted <see cref="IAmazonS3"/>. The point of the substitute is
/// the negative assertion: a key the ambient tenant does not own must be refused with <b>no call
/// reaching the client at all</b>, which an integration test against MinIO can only infer.
/// </summary>
public sealed class S3StorageServiceTests
{
    private sealed class Probe { }

    private const string Bucket = "test-bucket";
    private const string ServiceUrl = "http://minio:9000";

    private readonly IAmazonS3 _s3 = Substitute.For<IAmazonS3>();
    private readonly AmbientTenantStorageKeys _keys = new("acme");

    private S3StorageService Create(S3StorageOptions? options = null) =>
        new(_s3,
            Options.Create(options ?? new S3StorageOptions
            {
                Bucket = Bucket,
                ServiceUrl = ServiceUrl,
                ForcePathStyle = true,
                Region = "us-east-1",
                PublicRead = false,
            }),
            _keys,
            NullLogger<S3StorageService>.Instance);

    private static FileUploadRequest PngRequest(string fileName = "avatar.png")
        => new()
        {
            FileName = fileName,
            ContentType = "image/png",
            Data = new List<byte> { 1, 2, 3, 4 }
        };

    #region Composition

    [Fact]
    public async Task UploadAsync_Should_WriteUnderTheTenantsPublicPrefix()
    {
        var sut = Create();

        var url = await sut.UploadAsync<Probe>(PngRequest(), FileType.Image);

        var put = _s3.ReceivedCalls()
            .Select(c => c.GetArguments()[0])
            .OfType<PutObjectRequest>()
            .Single();

        put.Key.ShouldStartWith("uploads/tenants/acme/probe/");
        put.Key.ShouldEndWith("_avatar.png");
        url.ShouldBe($"{ServiceUrl}/{Bucket}/{put.Key}");
    }

    [Fact]
    public async Task UploadAsync_Should_ProduceDifferentKeys_When_TwoTenantsUploadTheSameFile()
    {
        var sut = Create();

        await sut.UploadAsync<Probe>(PngRequest(), FileType.Image);
        _keys.Current = "globex";
        await sut.UploadAsync<Probe>(PngRequest(), FileType.Image);

        var keys = _s3.ReceivedCalls()
            .Select(c => c.GetArguments()[0])
            .OfType<PutObjectRequest>()
            .Select(r => r.Key)
            .ToList();

        keys.Count.ShouldBe(2);
        keys[0].ShouldStartWith("uploads/tenants/acme/");
        keys[1].ShouldStartWith("uploads/tenants/globex/");
    }

    [Fact]
    public void ComposeKey_Should_PrefixTheAmbientTenant_In_BothSpaces()
    {
        var sut = Create();

        sut.ComposeKey(StorageSpace.Private, "myfiles/2026/09/ab/x.pdf")
            .ShouldBe("tenants/acme/myfiles/2026/09/ab/x.pdf");
        sut.ComposeKey(StorageSpace.Public, "probe/x.png")
            .ShouldBe("uploads/tenants/acme/probe/x.png");
    }

    [Fact]
    public async Task ADeploymentPrefix_Should_StayOutOfTheHandle_But_ReachS3()
    {
        // `Storage:S3:Prefix` is deployment plumbing: it belongs on the wire, never in the handle a
        // caller persists, or the same object would have two names depending on configuration.
        var sut = Create(new S3StorageOptions { Bucket = Bucket, ServiceUrl = ServiceUrl, Prefix = "env/staging" });

        var key = sut.ComposeKey(StorageSpace.Private, "myfiles/x.pdf");
        await sut.RemoveAsync(key);

        key.ShouldBe("tenants/acme/myfiles/x.pdf");
        await _s3.Received(1).DeleteObjectAsync(Bucket, "env/staging/tenants/acme/myfiles/x.pdf", Arg.Any<CancellationToken>());
    }

    #endregion

    #region The URL a caller persisted maps back to a key

    [Fact]
    public async Task RemoveAsync_Should_MapThePathStylePublicUrlBackToItsKey()
    {
        // MinIO and friends address path-style, so BuildPublicUrl puts the bucket in the path.
        // Before #78 that bucket segment survived into the key and the delete quietly hit nothing,
        // which is why replacing an avatar left the old object behind.
        var sut = Create();
        var url = await sut.UploadAsync<Probe>(PngRequest(), FileType.Image);

        await sut.RemoveAsync(url);

        var key = _s3.ReceivedCalls()
            .Select(c => c.GetArguments()[0])
            .OfType<PutObjectRequest>()
            .Single().Key;
        await _s3.Received(1).DeleteObjectAsync(Bucket, key, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RemoveAsync_Should_MapACdnPublicUrlBackToItsKey()
    {
        var sut = Create(new S3StorageOptions { Bucket = Bucket, PublicBaseUrl = "https://cdn.example.com/assets" });

        await sut.RemoveAsync("https://cdn.example.com/assets/uploads/tenants/acme/probe/x.png");

        await _s3.Received(1).DeleteObjectAsync(Bucket, "uploads/tenants/acme/probe/x.png", Arg.Any<CancellationToken>());
    }

    [Fact]
    public void BuildPublicUrl_Should_Refuse_AKeyTheTenantDoesNotOwn()
    {
        var sut = Create();

        Should.Throw<StorageKeyNotOwnedException>(() => sut.BuildPublicUrl("uploads/tenants/globex/probe/x.png"));
    }

    #endregion

    #region Cross-tenant refusal

    [Fact]
    public async Task EveryKeyTakingOperation_Should_Refuse_AnotherTenantsRealKey_WithoutCallingS3()
    {
        var sut = Create();
        const string theirs = "tenants/globex/myfiles/2026/09/ab/report.pdf";

        await Should.ThrowAsync<StorageKeyNotOwnedException>(() => sut.DownloadAsync(theirs));
        await Should.ThrowAsync<StorageKeyNotOwnedException>(() => sut.ExistsAsync(theirs));
        await Should.ThrowAsync<StorageKeyNotOwnedException>(() => sut.GetSizeAsync(theirs));
        await Should.ThrowAsync<StorageKeyNotOwnedException>(() => sut.RemoveAsync(theirs));
        await Should.ThrowAsync<StorageKeyNotOwnedException>(() => sut.HeadObjectAsync(theirs));
        await Should.ThrowAsync<StorageKeyNotOwnedException>(
            () => sut.GenerateUploadUrlAsync(theirs, "application/pdf", 1024, TimeSpan.FromMinutes(5)));
        await Should.ThrowAsync<StorageKeyNotOwnedException>(
            () => sut.GenerateDownloadUrlAsync(theirs, TimeSpan.FromMinutes(5)));
        Should.Throw<StorageKeyNotOwnedException>(() => sut.BuildPublicUrl(theirs));

        _s3.ReceivedCalls().ShouldBeEmpty();
    }

    [Theory]
    [InlineData("uploads/tenants/acme-2/probe/x.png")]        // a tenant id ours is a prefix of
    [InlineData("tenants/acmeextra/x.png")]
    [InlineData("tenants/acme/../globex/x.png")]              // traversal
    [InlineData("//tenants/acme/x.png")]
    [InlineData("tenants//acme/x.png")]
    [InlineData("tenants/acme/..%2f..%2fglobex/x.png")]       // encoded separators
    [InlineData("TENANTS/acme/x.png")]                        // case games
    [InlineData("uploads/Tenants/acme/x.png")]
    [InlineData("uploads/probe/legacy_avatar.png")]           // pre-#78 flat key
    [InlineData("")]
    [InlineData("   ")]
    public async Task DownloadAsync_Should_Refuse_AKeyOutsideTheTenantsPrefixes(string key)
    {
        var sut = Create();

        await Should.ThrowAsync<StorageKeyNotOwnedException>(() => sut.DownloadAsync(key));
        _s3.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task APercentEncodedSeparatorInAUrl_Should_Refuse_RatherThanBeDecoded()
    {
        // The URL → key mapping deliberately does not unescape: `%2f` stays `%2f`, which the key
        // grammar has no character for. Unescaping first would hand back `../..` and a real key.
        var sut = Create();

        await Should.ThrowAsync<StorageKeyNotOwnedException>(
            () => sut.DownloadAsync($"{ServiceUrl}/{Bucket}/tenants/acme/..%2f..%2ftenants%2fglobex/x.png"));
        _s3.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task RemoveIfOwnedAsync_Should_SkipRatherThanThrow_When_TheHandleIsNotOurs()
    {
        var sut = Create();

        (await sut.RemoveIfOwnedAsync("uploads/probe/legacy_avatar.png")).ShouldBeFalse();
        (await sut.RemoveIfOwnedAsync("https://cdn.example.com/avatars/me.png")).ShouldBeFalse();
        (await sut.RemoveIfOwnedAsync("tenants/globex/probe/theirs.png")).ShouldBeFalse();
        (await sut.RemoveIfOwnedAsync(null)).ShouldBeFalse();

        _s3.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task EveryOperation_Should_Throw_When_ThereIsNoAmbientTenant()
    {
        var sut = Create();
        _keys.Current = null;

        await Should.ThrowAsync<MissingStorageTenantException>(
            () => sut.UploadAsync<Probe>(PngRequest(), FileType.Image));
        await Should.ThrowAsync<MissingStorageTenantException>(() => sut.ExistsAsync("tenants/acme/x.png"));
        await Should.ThrowAsync<MissingStorageTenantException>(() => sut.RemoveAsync("tenants/acme/x.png"));
        await Should.ThrowAsync<MissingStorageTenantException>(() => sut.RemoveIfOwnedAsync("tenants/acme/x.png"));
        Should.Throw<MissingStorageTenantException>(() => sut.ComposeKey(StorageSpace.Private, "x.png"));

        _s3.ReceivedCalls().ShouldBeEmpty();
    }

    #endregion

    #region Presigning

    [Fact]
    public async Task GenerateUploadUrlAsync_Should_SignTheTenantPrefixedKey()
    {
        _s3.GetPreSignedURLAsync(Arg.Any<GetPreSignedUrlRequest>())
            .Returns($"{ServiceUrl}/{Bucket}/signed");
        var sut = Create();
        var key = sut.ComposeKey(StorageSpace.Private, "myfiles/2026/09/ab/x.pdf");

        await sut.GenerateUploadUrlAsync(key, "application/pdf", 1024, TimeSpan.FromMinutes(5));

        var request = _s3.ReceivedCalls()
            .Select(c => c.GetArguments()[0])
            .OfType<GetPreSignedUrlRequest>()
            .Single();
        request.Key.ShouldBe("tenants/acme/myfiles/2026/09/ab/x.pdf");
        request.Verb.ShouldBe(HttpVerb.PUT);
    }

    #endregion
}
