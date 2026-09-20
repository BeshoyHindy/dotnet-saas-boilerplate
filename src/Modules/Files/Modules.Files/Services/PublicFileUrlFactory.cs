using Boilerplate.BuildingBlocks.Storage.Services;
using Boilerplate.Modules.Files.Contracts.v1.DTOs;
using Boilerplate.Modules.Files.Domain;
using Microsoft.Extensions.Options;

namespace Boilerplate.Modules.Files.Services;

/// <summary>
/// Builds the <c>publicUrl</c> the Files read endpoints hand back for a <see cref="Visibility.Public"/>
/// asset — a <b>short-lived presigned GET</b>, minted on every read and never persisted.
/// <para>
/// Why not an unsigned bucket URL: Files objects live in the Storage block's
/// <see cref="Boilerplate.BuildingBlocks.Storage.Keys.StorageSpace.Private"/> space, which public
/// and private files share and where visibility is only a database column. The deploy stacks grant
/// anonymous read on the public space alone (avatars and tenant theme assets), because widening
/// that grant would publish every private file and turn <c>PATCH /files/{id}/visibility</c> into a
/// no-op. So the signature — not the bucket policy — carries the access, and the TTL bounds it.
/// </para>
/// <para>
/// Revocation: flipping Public → Private stops URL issuance immediately, but a signature already
/// handed out stays valid until it expires. That is why <see cref="FilesOptions.PublicUrlTtlMinutes"/>
/// defaults to minutes; callers that need instant, hard revocation must delete the object.
/// </para>
/// </summary>
public sealed class PublicFileUrlFactory(IStorageService storage, IOptions<FilesOptions> options)
{
    /// <summary>Lifetime of an issued public URL, clamped to a sane band regardless of configuration.</summary>
    public TimeSpan Ttl => TimeSpan.FromMinutes(Math.Clamp(options.Value.PublicUrlTtlMinutes, 1, 15));

    /// <summary>
    /// Returns a freshly signed, inline-rendering URL for a Public file, or <c>null</c> for anything
    /// else. Private files are reachable only through the auth-gated <c>GET /files/{id}/url</c>.
    /// </summary>
    public async ValueTask<string?> TryBuildAsync(FileAsset file, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);

        if (file.Visibility != Visibility.Public)
        {
            return null;
        }

        // inline so an <img src> / <embed> paints instead of triggering a download, and with the
        // original name so a manual save doesn't produce the storage key as a filename.
        var disposition = $"inline; filename=\"{StorageKeyBuilder.Sanitize(file.OriginalFileName)}\"";
        var url = await storage
            .GenerateDownloadUrlAsync(file.StorageKey, Ttl, disposition, cancellationToken)
            .ConfigureAwait(false);

        // Local storage returns a server-relative path (resolved by the client against the API origin).
        return url.IsAbsoluteUri ? url.AbsoluteUri : url.OriginalString;
    }
}
