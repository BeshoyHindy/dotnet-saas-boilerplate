using Boilerplate.BuildingBlocks.Shared.Identity.Authorization;
using Boilerplate.Modules.Files.Contracts.Authorization;
using Boilerplate.Modules.Files.Contracts.v1.Commands;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Boilerplate.Modules.Files.Features.v1.DeleteFile;

public static class DeleteFileEndpoint
{
    internal static RouteHandlerBuilder MapDeleteFileEndpoint(this IEndpointRouteBuilder endpoints)
        => endpoints.MapDelete("/{id:guid}",
                async (Guid id, IMediator mediator, CancellationToken cancellationToken) =>
                {
                    await mediator.Send(new DeleteFileCommand(id), cancellationToken);
                    return TypedResults.NoContent();
                })
            .WithName("DeleteFile")
            .WithSummary("Soft-delete a file; bytes purged after retention window")
            .RequirePermission(FilesPermissions.DeleteOwn);
}
