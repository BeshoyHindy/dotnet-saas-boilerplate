using System.Collections.ObjectModel;
using Boilerplate.Modules.Files.Contracts.v1.DTOs;
using Boilerplate.Modules.Files.Contracts.v1.Queries;
using Boilerplate.Modules.Files.Data;
using Boilerplate.Modules.Files.Features.v1.Internal;
using Boilerplate.Modules.Files.Services;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace Boilerplate.Modules.Files.Features.v1.ListSharedFiles;

/// <summary>
/// Returns Public, Available files belonging to the built-in tenant-wide owner types so the SPA
/// can render a "Shared in tenant" surface alongside "My files". Tenant scoping is handled by
/// the framework's BaseDbContext (schema-per-tenant), not by a WHERE clause here.
/// </summary>
public sealed class ListSharedFilesQueryHandler(FilesDbContext db, PublicFileUrlFactory publicUrls)
    : IQueryHandler<ListSharedFilesQuery, ReadOnlyCollection<FileAssetDto>>
{
    // Free-standing tenant files (not bound to a domain entity). Module-owned attachments
    // are excluded — their visibility follows the owning entity's access policy, not a share decision.
    private static readonly string[] SharedOwnerTypes = ["MyFiles", "User"];

    public async ValueTask<ReadOnlyCollection<FileAssetDto>> Handle(ListSharedFilesQuery q, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(q);

        var page = Math.Max(1, q.Page);
        var pageSize = Math.Clamp(q.PageSize, 1, 100);

        var rows = await db.FileAssets.AsNoTracking()
            .Where(f => f.Visibility == Visibility.Public
                && f.Status == FileAssetStatus.Available
                && SharedOwnerTypes.Contains(f.OwnerType))
            .OrderByDescending(f => f.CreatedAtUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // Every row here is Public by construction, so each gets a freshly signed short-lived URL.
        var dtos = new List<FileAssetDto>(rows.Count);
        foreach (var f in rows)
        {
            dtos.Add(FileAssetMapper.ToDto(f, await publicUrls.TryBuildAsync(f, cancellationToken).ConfigureAwait(false)));
        }

        return dtos.AsReadOnly();
    }
}
