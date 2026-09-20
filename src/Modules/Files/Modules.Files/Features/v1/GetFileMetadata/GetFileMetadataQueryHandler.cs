using Boilerplate.BuildingBlocks.Core.Context;
using Boilerplate.BuildingBlocks.Core.Exceptions;
using Boilerplate.Modules.Files.Contracts;
using Boilerplate.Modules.Files.Contracts.v1.DTOs;
using Boilerplate.Modules.Files.Contracts.v1.Queries;
using Boilerplate.Modules.Files.Data;
using Boilerplate.Modules.Files.Features.v1.Internal;
using Boilerplate.Modules.Files.Services;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace Boilerplate.Modules.Files.Features.v1.GetFileMetadata;

public sealed class GetFileMetadataQueryHandler(
    FilesDbContext db,
    FileAccessPolicyRegistry policies,
    ICurrentUser currentUser,
    PublicFileUrlFactory publicUrls)
    : IQueryHandler<GetFileMetadataQuery, FileAssetDto>
{
    public async ValueTask<FileAssetDto> Handle(GetFileMetadataQuery q, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(q);

        var f = await db.FileAssets.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == q.FileAssetId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new NotFoundException("file not found");

        var userId = currentUser.GetUserId().ToString();
        var policy = policies.Resolve(f.OwnerType)
            ?? throw new NotFoundException("file not found"); // don't leak existence on missing policy

        var ctx = new FileAccessContext(f.Id, f.OwnerType, f.OwnerId, f.CreatedByUserId, (int)f.Visibility);
        if (!await policy.CanReadAsync(ctx, userId, cancellationToken).ConfigureAwait(false))
        {
            throw new NotFoundException("file not found");
        }

        // Public files get a freshly signed, short-lived GET URL (never persisted — the tenants/ key
        // space has no anonymous grant). Private files get none; they go through the url endpoint.
        var publicUrl = await publicUrls.TryBuildAsync(f, cancellationToken).ConfigureAwait(false);

        return FileAssetMapper.ToDto(f, publicUrl);
    }
}
