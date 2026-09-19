using Boilerplate.BuildingBlocks.Shared.Identity.Authorization;
using Boilerplate.Modules.Files.Contracts.Authorization;
using Boilerplate.Modules.Files.Contracts.v1.Commands;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Boilerplate.Modules.Files.Features.v1.FinalizeUpload;

public static class FinalizeUploadEndpoint
{
    internal static RouteHandlerBuilder MapFinalizeUploadEndpoint(this IEndpointRouteBuilder endpoints)
        => endpoints.MapPost("/{id:guid}/finalize",
                async (Guid id, IMediator mediator, CancellationToken cancellationToken) =>
                    Results.Ok(await mediator.Send(new FinalizeUploadCommand(id), cancellationToken)))
            .WithName("FinalizeFileUpload")
            .WithSummary("Finalize a file upload after the browser PUT completes")
            .RequireAuthorization()
            // Second half of the presigned-upload flow — same grant as /upload-url.
            .RequirePermission(FilesPermissions.Upload);
}
