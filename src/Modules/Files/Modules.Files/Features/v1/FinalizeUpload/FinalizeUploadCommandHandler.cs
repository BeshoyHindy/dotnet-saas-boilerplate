using System.Diagnostics;
using System.Net;
using Boilerplate.BuildingBlocks.Core.Context;
using Boilerplate.BuildingBlocks.Core.Exceptions;
using Boilerplate.BuildingBlocks.Eventing.Abstractions;
using Boilerplate.BuildingBlocks.Storage.Services;
using Boilerplate.Modules.Files.Contracts.Events;
using Boilerplate.Modules.Files.Contracts.v1.Commands;
using Boilerplate.Modules.Files.Contracts.v1.DTOs;
using Boilerplate.Modules.Files.Data;
using Boilerplate.Modules.Files.Domain;
using Boilerplate.Modules.Files.Features.v1.Internal;
using Boilerplate.Modules.Files.Services;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace Boilerplate.Modules.Files.Features.v1.FinalizeUpload;

public sealed class FinalizeUploadCommandHandler(
    FilesDbContext db,
    IStorageService storage,
    IFileScanner scanner,
    IOutboxWriter outbox,
    ICurrentUser currentUser)
    : ICommandHandler<FinalizeUploadCommand, FileAssetDto>
{
    public async ValueTask<FileAssetDto> Handle(FinalizeUploadCommand cmd, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cmd);
        var tenantId = currentUser.GetTenant() ?? throw new UnauthorizedException("invalid tenant");
        var userId = currentUser.GetUserId().ToString();

        var asset = await db.FileAssets
            .FirstOrDefaultAsync(f => f.Id == cmd.FileAssetId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new NotFoundException("file not found");

        if (!string.Equals(asset.CreatedByUserId, userId, StringComparison.Ordinal))
        {
            throw new ForbiddenException("not your pending file");
        }
        if (asset.Status != FileAssetStatus.PendingUpload)
        {
            throw new CustomException("file already finalized", (IEnumerable<string>?)null, HttpStatusCode.Conflict);
        }

        var head = await storage.HeadObjectAsync(asset.StorageKey, cancellationToken).ConfigureAwait(false)
            ?? throw new CustomException("upload not received", (IEnumerable<string>?)null, HttpStatusCode.Conflict);

        // Allow declared+1% slack (S3 may differ slightly on multipart). Reject larger sizes.
        var maxAllowed = asset.SizeBytes + Math.Max(1024L, asset.SizeBytes / 100);
        if (head.SizeBytes > maxAllowed)
        {
            throw await RefuseAsync(asset, $"uploaded size ({head.SizeBytes}) exceeds declared ({asset.SizeBytes})", cancellationToken).ConfigureAwait(false);
        }

        if (!string.Equals(head.ContentType, asset.ContentType, StringComparison.OrdinalIgnoreCase))
        {
            throw await RefuseAsync(asset, "uploaded content-type mismatch", cancellationToken).ConfigureAwait(false);
        }

        // The two checks above only compare the caller's own metadata with itself; this one reads
        // the bytes (ASVS V5.2.2). It is not malware scanning — that is IFileScanner's, below.
        var prefix = await ReadPrefixAsync(asset.StorageKey, cancellationToken).ConfigureAwait(false);
        var verdict = UploadContentCheck.Verify(Path.GetExtension(asset.OriginalFileName), asset.ContentType, prefix);
        if (verdict != UploadContentVerdict.Match)
        {
            throw await RefuseAsync(asset, "uploaded content does not match its declared type", cancellationToken).ConfigureAwait(false);
        }

        var scanResult = await scanner.ScanAsync(asset.StorageKey, cancellationToken).ConfigureAwait(false);
        asset.MarkAvailable(head.SizeBytes, scanResult);

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var correlationId = Activity.Current?.Id ?? Guid.NewGuid().ToString();

        // Outbox rather than the bus: a crash between the SaveChanges above and delivery would
        // otherwise leave the file marked available with no consumer ever told about it.
        await outbox.AddAsync(new FileFinalizedIntegrationEvent(
            Id: Guid.NewGuid(),
            OccurredOnUtc: DateTime.UtcNow,
            TenantId: tenantId,
            CorrelationId: correlationId,
            Source: "Files",
            FileAssetId: asset.Id,
            OwnerType: asset.OwnerType,
            OwnerId: asset.OwnerId,
            ContentType: asset.ContentType,
            SizeBytes: asset.SizeBytes,
            FinalStatus: (int)asset.Status), cancellationToken).ConfigureAwait(false);

        return FileAssetMapper.ToDto(asset);
    }

    private async Task<byte[]> ReadPrefixAsync(string storageKey, CancellationToken cancellationToken)
    {
        // No ranged read on IStorageService: open the object, take the first bytes, and dispose,
        // which drops the rest of the transfer.
        var download = await storage.DownloadAsync(storageKey, cancellationToken).ConfigureAwait(false);
        if (download is null)
        {
            return [];
        }

        await using (download.Stream.ConfigureAwait(false))
        {
            var buffer = new byte[UploadContentCheck.PrefixLength];
            var read = await download.Stream
                .ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, cancellationToken)
                .ConfigureAwait(false);
            return buffer[..read];
        }
    }

    /// <summary>Deletes the object and the pending row; returns the 400 the caller throws.</summary>
    private async Task<CustomException> RefuseAsync(FileAsset asset, string reason, CancellationToken cancellationToken)
    {
        await storage.RemoveAsync(asset.StorageKey, cancellationToken).ConfigureAwait(false);
        db.FileAssets.Remove(asset);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new CustomException(reason, (IEnumerable<string>?)null, HttpStatusCode.BadRequest);
    }
}
