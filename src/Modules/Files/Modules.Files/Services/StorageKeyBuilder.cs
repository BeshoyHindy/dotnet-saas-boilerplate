using System.Text.RegularExpressions;

namespace Boilerplate.Modules.Files.Services;

/// <summary>
/// Builds the <b>tenant-relative</b> part of a Files object's key:
/// <c>{ownerType-lower}/{yyyy}/{MM}/{fileAssetId:N}/{sanitized-filename}</c>.
///
/// <para>The tenant prefix is deliberately absent, and so is the word "tenants". ADR-0002 makes
/// prefixing the Storage block's job: the caller states the space and hands over a relative path,
/// and <c>IStorageService.ComposeKey(StorageSpace.Private, …)</c> returns the key to persist.
/// Doing it here was the older arrangement — it produced the right key but proved nothing, because
/// nothing refused a key built any other way. Now the block refuses one.</para>
/// </summary>
public static partial class StorageKeyBuilder
{
    [GeneratedRegex(@"[^a-zA-Z0-9_\.-]")]
    private static partial Regex UnsafeChars();

    public static string Build(string ownerType, Guid fileAssetId, string fileName, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerType);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

#pragma warning disable CA1308 // path segments are intentionally lower-case
        var lowerOwner = Sanitize(ownerType.ToLowerInvariant());
#pragma warning restore CA1308
        var safe = Sanitize(fileName);
        return $"{lowerOwner}/{now:yyyy}/{now:MM}/{fileAssetId:N}/{safe}";
    }

    public static string Sanitize(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        return UnsafeChars().Replace(fileName, "_");
    }
}
