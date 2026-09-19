using Boilerplate.BuildingBlocks.Storage;

namespace Framework.Tests.Storage;

public sealed class FileTypeMetadataTests
{
    #region Happy Path

    [Fact]
    public void GetRules_Should_ReturnImageRules_When_ImageRequested()
    {
        // Act
        var rules = FileTypeMetadata.GetRules(FileType.Image);

        // Assert
        rules.MaxSizeInMB.ShouldBe(5);
        rules.AllowedExtensions.ShouldContain(".png");
        rules.AllowedExtensions.ShouldContain(".jpg");
        rules.AllowedExtensions.ShouldContain(".jpeg");
        rules.AllowedExtensions.ShouldContain(".ico");
    }

    [Fact]
    public void GetRules_Should_ReturnPdfRules_When_PdfRequested()
    {
        // Act
        var rules = FileTypeMetadata.GetRules(FileType.Pdf);

        // Assert
        rules.MaxSizeInMB.ShouldBe(10);
        rules.AllowedExtensions.ShouldHaveSingleItem();
        rules.AllowedExtensions.ShouldContain(".pdf");
    }

    #endregion

    #region ContentTypeFor

    [Theory]
    [InlineData(".png", "image/png")]
    [InlineData(".PNG", "image/png")]
    [InlineData(".jpg", "image/jpeg")]
    [InlineData(".jpeg", "image/jpeg")]
    [InlineData(".ico", "image/x-icon")]
    public void ContentTypeFor_Should_DeriveFromTheExtension(string extension, string expected)
    {
        FileTypeMetadata.ContentTypeFor(extension).ShouldBe(expected);
    }

    [Fact]
    public void ContentTypeFor_Should_FallBackToOctetStream_When_ExtensionIsUnrecognized()
    {
        // Never reachable through a validated upload — the extension allow-list rules it out first
        // — but the fallback must never be something a browser will render.
        FileTypeMetadata.ContentTypeFor(".exe").ShouldBe("application/octet-stream");
    }

    #endregion

    #region Edge Cases

    [Fact]
    public void GetRules_Should_Throw_When_TypeUnsupported()
    {
        // FileType.Document has no mapping and falls into the default arm.
        Should.Throw<NotSupportedException>(() => FileTypeMetadata.GetRules(FileType.Document));
    }

    [Fact]
    public void FileValidationRules_Should_DefaultToFiveMb_When_NotSet()
    {
        // Arrange & Act
        var rules = new FileValidationRules();

        // Assert
        rules.MaxSizeInMB.ShouldBe(5);
        rules.AllowedExtensions.ShouldBeEmpty();
    }

    #endregion
}
