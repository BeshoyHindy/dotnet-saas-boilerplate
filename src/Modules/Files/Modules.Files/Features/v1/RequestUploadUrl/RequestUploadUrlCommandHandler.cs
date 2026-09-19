using System.Net;
using Boilerplate.BuildingBlocks.Core.Context;
using Boilerplate.BuildingBlocks.Core.Exceptions;
using Boilerplate.BuildingBlocks.Storage.Keys;
using Boilerplate.BuildingBlocks.Storage.Services;
using Boilerplate.Modules.Files.Contracts;
using Boilerplate.Modules.Files.Contracts.v1.Commands;
using Boilerplate.Modules.Files.Contracts.v1.DTOs;
using Boilerplate.Modules.Files.Data;
using Boilerplate.Modules.Files.Domain;
using Boilerplate.Modules.Files.Services;
using Mediator;
using Microsoft.Extensions.Options;

namespace Boilerplate.Modules.Files.Features.v1.RequestUploadUrl;

public sealed class RequestUploadUrlCommandHandler(
    FilesDbContext db,
    IStorageService storage,
    FileAccessPolicyRegistry policies,
    ICurrentUser currentUser,
    IOptions<FilesOptions> options)
    : ICommandHandler<RequestUploadUrlCommand, PresignedUploadResponse>
{
    public async ValueTask<PresignedUploadResponse> Handle(RequestUploadUrlCommand cmd, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cmd);

        // Not used to build the key any more — the Storage block prefixes it from the ambient
        // tenant (ADR-0002). Still checked here so a principal without a tenant claim is refused
        // before anything is written, as it always was.
        _ = currentUser.GetTenant() ?? throw new UnauthorizedException("invalid tenant");
        var userId = currentUser.GetUserId();
        if (userId == Guid.Empty)
        {
            throw new UnauthorizedException("no current user");
        }

        // Category lookup + extension/size validation.
        if (!options.Value.Categories.TryGetValue(cmd.Category, out var category))
        {
            throw new CustomException($"Unknown category '{cmd.Category}'.", (IEnumerable<string>?)null, HttpStatusCode.BadRequest);
        }

        var extension = Path.GetExtension(cmd.FileName);
        if (string.IsNullOrWhiteSpace(extension) ||
            !category.AllowedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            throw new CustomException(
                $"Extension '{extension}' not allowed for category '{cmd.Category}'.",
                (IEnumerable<string>?)null,
                HttpStatusCode.BadRequest);
        }

        if (cmd.SizeBytes > category.MaxBytes)
        {
            throw new CustomException(
                $"File exceeds max size of {category.MaxBytes} bytes for category '{cmd.Category}'.",
                (IEnumerable<string>?)null,
                HttpStatusCode.BadRequest);
        }

        // Authorization: policy must exist and allow the attach.
        var policy = policies.Resolve(cmd.OwnerType)
            ?? throw new ForbiddenException($"No file access policy registered for owner type '{cmd.OwnerType}'.");
        if (!await policy.CanAttachAsync(cmd.OwnerId, userId.ToString(), cancellationToken).ConfigureAwait(false))
        {
            throw new ForbiddenException("Not allowed to attach files to this owner.");
        }

        // Generate id + storage key + presigned URL. The key we persist is whatever the block hands
        // back for this tenant-relative path in the private space — an opaque handle, never one we
        // compose a tenant into ourselves.
        var id = Guid.CreateVersion7();
        var relativePath = StorageKeyBuilder.Build(cmd.OwnerType, id, cmd.FileName, DateTimeOffset.UtcNow);
        var storageKey = storage.ComposeKey(StorageSpace.Private, relativePath);
        var ttl = TimeSpan.FromMinutes(options.Value.UploadUrlTtlMinutes);
        var presigned = await storage.GenerateUploadUrlAsync(storageKey, cmd.ContentType, category.MaxBytes, ttl, cancellationToken).ConfigureAwait(false);

        var asset = FileAsset.CreatePending(
            id: id,
            ownerType: cmd.OwnerType,
            ownerId: cmd.OwnerId,
            originalFileName: cmd.FileName,
            sanitizedFileName: StorageKeyBuilder.Sanitize(cmd.FileName),
            contentType: cmd.ContentType,
            declaredSizeBytes: cmd.SizeBytes,
            storageKey: storageKey,
            visibility: cmd.Visibility,
            createdByUserId: userId.ToString(),
            uploadDeadline: DateTimeOffset.UtcNow.Add(ttl));

        db.FileAssets.Add(asset);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new PresignedUploadResponse(asset.Id, presigned.Url, presigned.RequiredHeaders, presigned.ExpiresAt);
    }
}
