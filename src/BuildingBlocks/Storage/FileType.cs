namespace Boilerplate.BuildingBlocks.Storage;

public enum FileType
{
    Image,
    Document,
    Pdf
}

public sealed class FileValidationRules
{
    public IReadOnlyList<string> AllowedExtensions { get; init; } = Array.Empty<string>();
    public int MaxSizeInMB { get; init; } = 5;
}

public static class FileTypeMetadata
{
    public static FileValidationRules GetRules(FileType type) =>
        type switch
        {
            FileType.Image => new() { AllowedExtensions = [".jpg", ".jpeg", ".png", ".ico"], MaxSizeInMB = 5 },
            FileType.Pdf => new() { AllowedExtensions = [".pdf"], MaxSizeInMB = 10 },
            _ => throw new NotSupportedException($"Unsupported file type: {type}")
        };

    /// <summary>
    /// The content type stored for an upload, derived from the extension <see cref="GetRules"/>
    /// already validated — never the client-supplied <c>Content-Type</c> header. A public upload
    /// (avatar, brand asset) is served anonymously straight from storage, so trusting the header
    /// would let <c>evil.png</c> be stored, and served, as <c>text/html</c> (#78 hardening item 4).
    /// An extension outside the allow-list can't reach here; it falls back to the generic binary
    /// type rather than to anything caller-supplied.
    /// </summary>
    public static string ContentTypeFor(string extension) =>
        extension.ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".ico" => "image/x-icon",
            _ => "application/octet-stream"
        };
}