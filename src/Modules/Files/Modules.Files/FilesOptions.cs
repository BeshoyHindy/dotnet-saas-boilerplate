namespace Boilerplate.Modules.Files;

/// <summary>
/// Configuration for the Files module. Bound from the <c>Files</c> section of appsettings.json.
/// </summary>
public sealed class FilesOptions
{
    /// <summary>Lifetime of a presigned PUT URL minted by <c>POST /files/upload-url</c>.</summary>
    public int UploadUrlTtlMinutes { get; set; } = 15;

    /// <summary>Lifetime of a presigned GET URL minted by <c>GET /files/{id}/url</c>.</summary>
    public int DownloadUrlTtlMinutes { get; set; } = 5;

    /// <summary>
    /// Lifetime of the presigned GET URL returned as <c>publicUrl</c> for a <c>Visibility=Public</c>
    /// asset. Public Files objects live under the private <c>tenants/</c> key space, so the signature
    /// carries the access and this TTL bounds it: flipping a file back to Private stops issuance at
    /// once, but a URL already handed out stays usable until it expires. Kept short for that reason
    /// and clamped to 1–15 minutes by <c>PublicFileUrlFactory</c>.
    /// </summary>
    public int PublicUrlTtlMinutes { get; set; } = 5;

    /// <summary>How long a <c>PendingUpload</c> row is allowed to linger before the orphan purge job hard-deletes it.</summary>
    public int OrphanRetentionMinutes { get; set; } = 60;

    /// <summary>How long a soft-deleted FileAsset stays in trash before bytes + row are hard-purged.</summary>
    public int SoftDeleteRetentionDays { get; set; } = 30;

    /// <summary>Categories of files the module accepts, with per-category extension whitelists and size caps.</summary>
    public Dictionary<string, FileCategoryOptions> Categories { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class FileCategoryOptions
{
    /// <summary>
    /// Extensions this category accepts. Each must have a known content signature
    /// (<c>UploadContentCheck</c>): finalize reads the object's first bytes and refuses a file whose
    /// bytes are not the declared type.
    /// </summary>
    public List<string> AllowedExtensions { get; set; } = [];
    public long MaxBytes { get; set; }

    /// <summary>
    /// Allows types a browser runs script from when opened inline (SVG, HTML). Off by default and in
    /// every shipped category: any Files asset can be served inline (a Public one always is), and
    /// such a file then runs its script for whoever opens the link. Setting it is a category's
    /// explicit acceptance of that.
    /// </summary>
    public bool AllowScriptCapableTypes { get; set; }
}
