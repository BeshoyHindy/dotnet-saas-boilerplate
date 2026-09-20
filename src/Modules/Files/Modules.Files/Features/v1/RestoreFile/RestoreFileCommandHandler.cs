using Boilerplate.BuildingBlocks.Core.Exceptions;
using Boilerplate.BuildingBlocks.Persistence;
using Boilerplate.Modules.Files.Contracts.v1.Commands;
using Boilerplate.Modules.Files.Data;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace Boilerplate.Modules.Files.Features.v1.RestoreFile;

public sealed class RestoreFileCommandHandler(FilesDbContext db)
    : ICommandHandler<RestoreFileCommand, Unit>
{
    public async ValueTask<Unit> Handle(RestoreFileCommand cmd, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cmd);

        // Lift ONLY the named SoftDelete filter — the row we are restoring is deleted by definition.
        // A bare IgnoreQueryFilters() would also strip Finbuckle's anonymous tenant filter, letting
        // tenant A restore (and thereafter read) tenant B's file by id (ADR-0002, persistence rule).
        var f = await db.FileAssets
            .IgnoreQueryFilters([QueryFilters.SoftDelete])
            .FirstOrDefaultAsync(x => x.Id == cmd.FileAssetId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new NotFoundException("file not found");

        if (!f.IsDeleted)
        {
            return Unit.Value; // idempotent — already live
        }

        f.Restore();
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Unit.Value;
    }
}
