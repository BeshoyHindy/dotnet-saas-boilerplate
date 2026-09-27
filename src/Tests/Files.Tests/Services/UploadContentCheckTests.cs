using System.Text;
using System.Text.Json;
using Boilerplate.Modules.Files.Services;

namespace Files.Tests.Services;

/// <summary>
/// <see cref="UploadContentCheck"/> is what makes finalize look at the bytes rather than at the
/// caller's own metadata (ASVS 5.0 V5.2.2): the declared content type must be one the file's
/// extension allows, and the object's first bytes must carry that type's signature. Expected
/// signatures below are the published ones (PNG spec, JFIF/EXIF, GIF87a/89a, RIFF/WebP, ICONDIR,
/// ISO 32000, ZIP APPNOTE), not values read back from the implementation.
/// </summary>
public class UploadContentCheckTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D];
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46];
    private static readonly byte[] Gif = "GIF89a\x01\x00\x01\x00"u8.ToArray();
    private static readonly byte[] WebP = [.. "RIFF"u8, 0x24, 0x00, 0x00, 0x00, .. "WEBPVP8 "u8];
    private static readonly byte[] Ico = [0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x10, 0x10];
    private static readonly byte[] Pdf = [.. "%PDF-1.7\n%"u8, 0xE2, 0xE3, 0xCF, 0xD3, 0x0A];
    private static readonly byte[] Zip = [0x50, 0x4B, 0x03, 0x04, 0x14, 0x00, 0x06, 0x00];
    private static readonly byte[] EmptyZip = [0x50, 0x4B, 0x05, 0x06, 0x00, 0x00, 0x00, 0x00];
    private static readonly byte[] Html = "<!DOCTYPE html><html><body><script>alert(1)</script></body></html>"u8.ToArray();

    private const string Docx = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
    private const string Xlsx = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    private const string Pptx = "application/vnd.openxmlformats-officedocument.presentationml.presentation";

    public static TheoryData<string, string, byte[]> GenuineFiles => new()
    {
        { ".png", "image/png", Png },
        { ".jpg", "image/jpeg", Jpeg },
        { ".jpeg", "image/jpeg", Jpeg },
        { ".gif", "image/gif", Gif },
        { ".webp", "image/webp", WebP },
        { ".ico", "image/x-icon", Ico },
        { ".ico", "image/vnd.microsoft.icon", Ico },
        { ".pdf", "application/pdf", Pdf },
        { ".docx", Docx, Zip },
        { ".xlsx", Xlsx, Zip },
        { ".pptx", Pptx, Zip },
        { ".zip", "application/zip", Zip },
        { ".zip", "application/x-zip-compressed", Zip },
        { ".zip", "application/zip", EmptyZip },
        { ".txt", "text/plain", "plain words\r\nand a second line\twith a tab\n"u8.ToArray() },
        { ".csv", "text/csv", "name,amount\nwidget,12.50\n"u8.ToArray() },
        // Excel's default CSV is Windows-1252, not UTF-8: a high byte is still text.
        { ".csv", "application/vnd.ms-excel", [.. "name;amount\r\ncaf"u8, 0xE9, .. ";3\r\n"u8] },
    };

    #region Happy Path

    [Theory]
    [MemberData(nameof(GenuineFiles))]
    public void Verify_Should_Match_When_BytesCarryTheDeclaredTypesSignature(string extension, string contentType, byte[] bytes)
    {
        UploadContentCheck.Verify(extension, contentType, bytes).ShouldBe(UploadContentVerdict.Match);
    }

    [Fact]
    public void Verify_Should_IgnoreCaseAndParameters_When_ComparingTheDeclaredType()
    {
        UploadContentCheck.Verify(".PNG", "Image/PNG", Png).ShouldBe(UploadContentVerdict.Match);
        UploadContentCheck.Verify(".txt", "text/plain; charset=utf-8", "hello"u8.ToArray()).ShouldBe(UploadContentVerdict.Match);
    }

    [Fact]
    public void Verify_Should_Match_When_TextStartsWithAUtf8ByteOrderMark()
    {
        byte[] bytes = [0xEF, 0xBB, 0xBF, .. "a,b\n1,2\n"u8];

        UploadContentCheck.Verify(".csv", "text/csv", bytes).ShouldBe(UploadContentVerdict.Match);
    }

    #endregion

    #region Mismatches

    [Fact]
    public void Verify_Should_Refuse_When_PngNamedFileIsHtml()
    {
        UploadContentCheck.Verify(".png", "image/png", Html).ShouldBe(UploadContentVerdict.SignatureMismatch);
    }

    [Theory]
    [InlineData(".pdf", "application/pdf")]
    [InlineData(".jpg", "image/jpeg")]
    [InlineData(".zip", "application/zip")]
    public void Verify_Should_Refuse_When_BytesAreAnotherType(string extension, string contentType)
    {
        UploadContentCheck.Verify(extension, contentType, Png).ShouldBe(UploadContentVerdict.SignatureMismatch);
    }

    [Fact]
    public void Verify_Should_Refuse_When_DeclaredTypeIsNotOneTheExtensionAllows()
    {
        // A .txt declared text/html would be stored — and served inline — as HTML.
        UploadContentCheck.Verify(".txt", "text/html", "hello"u8.ToArray()).ShouldBe(UploadContentVerdict.ContentTypeMismatch);
        UploadContentCheck.Verify(".png", "image/svg+xml", Png).ShouldBe(UploadContentVerdict.ContentTypeMismatch);
    }

    [Fact]
    public void Verify_Should_Refuse_When_DeclaredTypeIsUnspecific()
    {
        UploadContentCheck.Verify(".pdf", "application/octet-stream", Pdf).ShouldBe(UploadContentVerdict.ContentTypeMismatch);
    }

    [Theory]
    [InlineData(".exe")]
    [InlineData(".heic")]
    [InlineData("")]
    public void Verify_Should_Refuse_When_TheTypeHasNoKnownSignature(string extension)
    {
        UploadContentCheck.Verify(extension, "application/octet-stream", Png).ShouldBe(UploadContentVerdict.UnknownType);
    }

    [Fact]
    public void Verify_Should_Refuse_When_TextContainsBinaryBytes()
    {
        byte[] bytes = [.. "looks like text"u8, 0x00, 0x01, 0x02];

        UploadContentCheck.Verify(".txt", "text/plain", bytes).ShouldBe(UploadContentVerdict.SignatureMismatch);
    }

    [Theory]
    [InlineData("<!DOCTYPE html><p>hi</p>")]
    [InlineData("  \n<html>")]
    [InlineData("<SCRIPT>alert(1)</SCRIPT>")]
    [InlineData("<?xml version=\"1.0\"?><svg xmlns=\"http://www.w3.org/2000/svg\"/>")]
    [InlineData("<svg onload=\"alert(1)\">")]
    [InlineData("<!-- x -->")]
    public void Verify_Should_Refuse_When_TextSniffsAsMarkup(string content)
    {
        UploadContentCheck.Verify(".txt", "text/plain", Encoding.UTF8.GetBytes(content)).ShouldBe(UploadContentVerdict.SignatureMismatch);
    }

    [Fact]
    public void Verify_Should_Match_When_TextMerelyMentionsATag()
    {
        UploadContentCheck.Verify(".txt", "text/plain", "use <b> for bold"u8.ToArray()).ShouldBe(UploadContentVerdict.Match);
    }

    [Fact]
    public void Verify_Should_Refuse_When_TheObjectIsShorterThanTheSignature()
    {
        UploadContentCheck.Verify(".png", "image/png", [0x89, 0x50]).ShouldBe(UploadContentVerdict.SignatureMismatch);
        UploadContentCheck.Verify(".txt", "text/plain", []).ShouldBe(UploadContentVerdict.SignatureMismatch);
    }

    #endregion

    #region Script-capable types

    [Theory]
    [InlineData(".svg")]
    [InlineData(".html")]
    [InlineData(".htm")]
    public void IsScriptCapable_Should_BeTrue_For_TypesABrowserRunsScriptFrom(string extension)
    {
        UploadContentCheck.IsScriptCapable(extension).ShouldBeTrue();
    }

    [Theory]
    [InlineData(".png")]
    [InlineData(".pdf")]
    [InlineData(".txt")]
    [InlineData(".exe")]
    public void IsScriptCapable_Should_BeFalse_For_EverythingElse(string extension)
    {
        UploadContentCheck.IsScriptCapable(extension).ShouldBeFalse();
    }

    [Fact]
    public void Verify_Should_Match_Svg_Only_When_ItIsMarkup()
    {
        UploadContentCheck.Verify(".svg", "image/svg+xml", "<svg xmlns=\"http://www.w3.org/2000/svg\"/>"u8.ToArray()).ShouldBe(UploadContentVerdict.Match);
        UploadContentCheck.Verify(".svg", "image/svg+xml", Png).ShouldBe(UploadContentVerdict.SignatureMismatch);
    }

    #endregion

    #region Shipped configuration

    [Fact]
    public void Every_Shipped_Category_Should_AllowOnly_TypesWithAKnownSignature_And_NoScriptCapableType()
    {
        // The categories the host ships (appsettings.json, Files:Categories). A new extension
        // there with no signature would be refused at every upload; a script-capable one would be
        // served inline to whoever opens it.
        using var json = JsonDocument.Parse(
            File.ReadAllText(ShippedAppSettingsPath()),
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });

        var categories = json.RootElement.GetProperty("Files").GetProperty("Categories").EnumerateObject().ToList();
        categories.ShouldNotBeEmpty();

        foreach (var category in categories)
        {
            if (category.Value.TryGetProperty("AllowScriptCapableTypes", out var optIn))
            {
                optIn.GetBoolean().ShouldBeFalse($"shipped category '{category.Name}' must not opt in to script-capable types");
            }

            foreach (var extension in category.Value.GetProperty("AllowedExtensions").EnumerateArray().Select(e => e.GetString()!))
            {
                UploadContentCheck.IsKnownType(extension).ShouldBeTrue($"'{extension}' in category '{category.Name}' has no known signature");
                UploadContentCheck.IsScriptCapable(extension).ShouldBeFalse($"'{extension}' in category '{category.Name}' is script-capable");
            }
        }
    }

    private static string ShippedAppSettingsPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "Host", "Boilerplate.Api", "appsettings.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }
            dir = dir.Parent;
        }
        throw new FileNotFoundException("Could not locate src/Host/Boilerplate.Api/appsettings.json above the test output directory.");
    }

    #endregion
}
