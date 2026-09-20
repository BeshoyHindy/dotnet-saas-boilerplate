using Boilerplate.BuildingBlocks.Shared.Storage;
using Boilerplate.BuildingBlocks.Storage;
using Boilerplate.Modules.Multitenancy.Contracts.Dtos;
using Boilerplate.Modules.Multitenancy.Contracts.v1.UpdateTenantTheme;
using Boilerplate.Modules.Multitenancy.Features.v1.UpdateTenantTheme;

namespace Multitenancy.Tests.Validators;

/// <summary>
/// A brand asset is uploaded through the theme save itself (#83), so this validator is the only
/// thing between a caller's bytes and <c>IStorageService.UploadAsync</c> — whose own extension/size
/// check throws <see cref="InvalidOperationException"/>, i.e. a 500 for a caller's mistake. Every
/// case below is one the storage block would otherwise have rejected too late.
/// </summary>
public sealed class BrandAssetUploadsValidatorTests
{
    private readonly UpdateTenantThemeCommandValidator _sut = new();

    private static readonly FileValidationRules ImageRules = FileTypeMetadata.GetRules(FileType.Image);

    private static FileUploadRequest Upload(string fileName, int bytes = 8) => new()
    {
        FileName = fileName,
        ContentType = "image/png",
        Data = [.. Enumerable.Repeat((byte)0x42, bytes)],
    };

    private static UpdateTenantThemeCommand Command(BrandAssetUploadsDto assets) =>
        new(new TenantThemeUpdateDto { BrandAssets = assets });

    [Fact]
    public async Task Validate_Should_Accept_A_Save_That_Stages_No_Asset()
    {
        // The common case: a palette edit. Nothing to upload, nothing to check.
        var result = await _sut.ValidateAsync(Command(new BrandAssetUploadsDto()));

        result.IsValid.ShouldBeTrue();
    }

    [Fact]
    public async Task Validate_Should_Accept_A_Removal_Flag_Without_Bytes()
    {
        var result = await _sut.ValidateAsync(Command(new BrandAssetUploadsDto
        {
            DeleteLogo = true,
            DeleteLogoDark = true,
            DeleteFavicon = true,
        }));

        result.IsValid.ShouldBeTrue();
    }

    [Theory]
    [InlineData(".jpg")]
    [InlineData(".jpeg")]
    [InlineData(".png")]
    [InlineData(".ico")]
    public async Task Validate_Should_Accept_Every_Extension_The_Storage_Block_Allows(string extension)
    {
        // Read off FileType.cs rather than restated, so a change there fails here first.
        ImageRules.AllowedExtensions.ShouldContain(extension);

        var result = await _sut.ValidateAsync(Command(new BrandAssetUploadsDto { Logo = Upload($"brand{extension}") }));

        result.IsValid.ShouldBeTrue();
    }

    [Theory]
    [InlineData(".svg")]   // scriptable, and served anonymously out of the public space
    [InlineData(".gif")]
    [InlineData(".html")]
    [InlineData("")]       // no extension at all
    public async Task Validate_Should_Reject_An_Extension_Outside_The_AllowList(string extension)
    {
        var result = await _sut.ValidateAsync(Command(new BrandAssetUploadsDto { Logo = Upload($"brand{extension}") }));

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.PropertyName.EndsWith("Logo.FileName", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Validate_Should_Reject_An_Empty_FileName()
    {
        var result = await _sut.ValidateAsync(Command(new BrandAssetUploadsDto { Logo = Upload("") }));

        result.IsValid.ShouldBeFalse();
    }

    [Fact]
    public async Task Validate_Should_Reject_An_Upload_With_No_Bytes()
    {
        var result = await _sut.ValidateAsync(Command(new BrandAssetUploadsDto { Favicon = Upload("icon.ico", bytes: 0) }));

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.PropertyName.EndsWith("Favicon.Data", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Validate_Should_Reject_Bytes_Over_The_Image_Size_Limit()
    {
        var overLimit = (ImageRules.MaxSizeInMB * 1024 * 1024) + 1;

        var result = await _sut.ValidateAsync(Command(new BrandAssetUploadsDto { Logo = Upload("brand.png", overLimit) }));

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.PropertyName.EndsWith("Logo.Data", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Validate_Should_Check_Every_Slot_Not_Only_The_Logo()
    {
        // Each slot is its own owner in storage, so each one has to be gated on its own.
        var result = await _sut.ValidateAsync(Command(new BrandAssetUploadsDto
        {
            LogoDark = Upload("dark.svg"),
            Favicon = Upload("icon.exe"),
        }));

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.PropertyName.EndsWith("LogoDark.FileName", StringComparison.Ordinal));
        result.Errors.ShouldContain(e => e.PropertyName.EndsWith("Favicon.FileName", StringComparison.Ordinal));
    }
}
