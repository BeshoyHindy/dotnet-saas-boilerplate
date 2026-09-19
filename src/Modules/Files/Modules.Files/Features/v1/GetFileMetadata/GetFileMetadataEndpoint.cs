using Boilerplate.BuildingBlocks.Shared.Identity.Authorization;
using Boilerplate.Modules.Files.Contracts.Authorization;
using Boilerplate.Modules.Files.Contracts.v1.Queries;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Boilerplate.Modules.Files.Features.v1.GetFileMetadata;

public static class GetFileMetadataEndpoint
{
    internal static RouteHandlerBuilder MapGetFileMetadataEndpoint(this IEndpointRouteBuilder endpoints)
        => endpoints.MapGet("/{id:guid}",
                async (Guid id, IMediator mediator, CancellationToken cancellationToken) =>
                    Results.Ok(await mediator.Send(new GetFileMetadataQuery(id), cancellationToken)))
            .WithName("GetFileMetadata")
            .WithSummary("Get FileAsset metadata (plus a public URL if Visibility=Public)")
            .RequireAuthorization()
            // Files.Upload is the module's "may use Files at all" grant (same gate as the list
            // endpoints); which individual asset the caller may see is decided per-file by
            // IFileAccessPolicy inside the handler.
            .RequirePermission(FilesPermissions.Upload);
}
