using Boilerplate.BuildingBlocks.Storage.Services;
using Boilerplate.Modules.Files;
using Boilerplate.Modules.Files.Contracts.v1.DTOs;
using Boilerplate.Modules.Files.Domain;
using Boilerplate.Modules.Files.Services;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Files.Tests.Services;

/// <summary>
/// <see cref="PublicFileUrlFactory"/> is the single place that decides what <c>publicUrl</c> means
/// for a Files asset (issue #52): a short-lived presigned GET for Public files, nothing at all for
/// Private ones — for <b>every</b> storage provider, since the tenants/ key space is never granted
/// anonymous read.
/// </summary>
public class PublicFileUrlFactoryTests
{
    private readonly IStorageService _storage = Substitute.For<IStorageService>();

    private PublicFileUrlFactory CreateSut(int ttlMinutes = 5)
        => new(_storage, Options.Create(new FilesOptions { PublicUrlTtlMinutes = ttlMinutes }));

    private static FileAsset Asset(Visibility visibility, string originalFileName = "holiday photo.png")
    {
        var asset = FileAsset.CreatePending(
            Guid.NewGuid(),
            ownerType: "MyFiles",
            ownerId: null,
            originalFileName: originalFileName,
            sanitizedFileName: "holiday_photo.png",
            contentType: "image/png",
            declaredSizeBytes: 10,
            storageKey: "tenants/root/myfiles/2026/05/abc/holiday_photo.png",
            visibility: visibility,
            createdByUserId: "user-1",
            uploadDeadline: DateTimeOffset.UtcNow.AddMinutes(15));
        asset.MarkAvailable(10, ScanStatus.Clean);
        return asset;
    }

    #region Happy Path

    [Fact]
    public async Task TryBuildAsync_Should_MintAShortLivedPresignedGet_When_FileIsPublic()
    {
        // Arrange
        var sut = CreateSut();
        using var cts = new CancellationTokenSource();
        var asset = Asset(Visibility.Public);
        _storage.GenerateDownloadUrlAsync(
                asset.StorageKey, TimeSpan.FromMinutes(5), Arg.Any<string>(), cts.Token)
            .Returns(new Uri("https://minio.local/bucket/tenants/root/x.png?X-Amz-Signature=abc"));

        // Act
        var url = await sut.TryBuildAsync(asset, cts.Token);

        // Assert — the URL is signed, and the specific CancellationToken is forwarded.
        url.ShouldBe("https://minio.local/bucket/tenants/root/x.png?X-Amz-Signature=abc");
        await _storage.Received(1).GenerateDownloadUrlAsync(
            asset.StorageKey, TimeSpan.FromMinutes(5), Arg.Any<string>(), cts.Token);
    }

    [Fact]
    public async Task TryBuildAsync_Should_AskForInlineDispositionWithASanitizedFileName()
    {
        // Arrange — a filename carrying a quote must not be able to break out of the signed
        // response-content-disposition header.
        var sut = CreateSut();
        var asset = Asset(Visibility.Public, originalFileName: "in\"voice.pdf");
        _storage.GenerateDownloadUrlAsync(
                Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new Uri("https://minio.local/o?X-Amz-Signature=abc"));

        // Act
        await sut.TryBuildAsync(asset, CancellationToken.None);

        // Assert
        await _storage.Received(1).GenerateDownloadUrlAsync(
            Arg.Any<string>(),
            Arg.Any<TimeSpan>(),
            Arg.Is<string>(d => d == "inline; filename=\"in_voice.pdf\""),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TryBuildAsync_Should_ReturnAServerRelativePath_When_ProviderReturnsARelativeUri()
    {
        // Arrange — local storage hands back a path relative to the API origin, not a Uri.
        var sut = CreateSut();
        _storage.GenerateDownloadUrlAsync(
                Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new Uri("/tenants/root/myfiles/x.png", UriKind.Relative));

        // Act
        var url = await sut.TryBuildAsync(Asset(Visibility.Public), CancellationToken.None);

        // Assert
        url.ShouldBe("/tenants/root/myfiles/x.png");
    }

    #endregion

    #region Visibility gate

    [Fact]
    public async Task TryBuildAsync_Should_ReturnNull_When_FileIsPrivate()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        var url = await sut.TryBuildAsync(Asset(Visibility.Private), CancellationToken.None);

        // Assert — no URL is minted at all: a private file must never be reachable without auth,
        // whichever provider is configured.
        url.ShouldBeNull();
        await _storage.DidNotReceiveWithAnyArgs().GenerateDownloadUrlAsync(default!, default, default, default);
    }

    #endregion

    #region Edge Cases

    [Theory]
    [InlineData(0, 1)]      // nonsense config still signs something usable…
    [InlineData(-30, 1)]
    [InlineData(5, 5)]
    [InlineData(120, 15)]   // …and can never be widened into a long-lived public link.
    public async Task TryBuildAsync_Should_ClampTheConfiguredTtl(int configured, int expectedMinutes)
    {
        // Arrange
        var sut = CreateSut(configured);
        _storage.GenerateDownloadUrlAsync(
                Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new Uri("https://minio.local/o?X-Amz-Signature=abc"));

        // Act
        await sut.TryBuildAsync(Asset(Visibility.Public), CancellationToken.None);

        // Assert
        await _storage.Received(1).GenerateDownloadUrlAsync(
            Arg.Any<string>(),
            TimeSpan.FromMinutes(expectedMinutes),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TryBuildAsync_Should_Throw_When_FileIsNull()
    {
        var sut = CreateSut();
        await Should.ThrowAsync<ArgumentNullException>(async () => await sut.TryBuildAsync(null!, CancellationToken.None));
    }

    #endregion
}
