using Boilerplate.BuildingBlocks.Shared.Identity.Authorization;
using Boilerplate.Modules.Files.Contracts.Authorization;
using Boilerplate.Modules.Files.Contracts.v1.Queries;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Boilerplate.Modules.Files.Features.v1.ListMyFiles;

public static class ListMyFilesEndpoint
{
    internal static RouteHandlerBuilder MapListMyFilesEndpoint(this IEndpointRouteBuilder endpoints)
        => endpoints.MapGet("/mine",
                async (int? page, int? pageSize, IMediator mediator, CancellationToken cancellationToken) =>
                    TypedResults.Ok(await mediator.Send(new ListMyFilesQuery(page ?? 1, pageSize ?? 20), cancellationToken)))
            .WithName("ListMyFiles")
            .WithSummary("List files uploaded by the current user")
            .RequirePermission(FilesPermissions.Upload);
}
