using Boilerplate.BuildingBlocks.Shared.Identity.Authorization;
using Boilerplate.Modules.Files.Contracts.Authorization;
using Boilerplate.Modules.Files.Contracts.v1.Queries;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Boilerplate.Modules.Files.Features.v1.GetFileDownloadUrl;

public static class GetFileDownloadUrlEndpoint
{
    internal static RouteHandlerBuilder MapGetFileDownloadUrlEndpoint(this IEndpointRouteBuilder endpoints)
        => endpoints.MapGet("/{id:guid}/url",
                async (Guid id, bool? inline, IMediator mediator, CancellationToken cancellationToken) =>
                    TypedResults.Ok(await mediator.Send(new GetFileDownloadUrlQuery(id, inline ?? false), cancellationToken)))
            .WithName("GetFileDownloadUrl")
            .WithSummary("Mint a short-lived presigned download URL")
            .WithDescription("Default disposition is attachment (click-to-save). Pass ?inline=true to get an inline disposition for browser preview (PDF viewer, image render, etc.).")
            .RequireAuthorization()
            // Same module-level grant as the list endpoints; per-asset access is enforced by
            // IFileAccessPolicy when the URL is minted.
            .RequirePermission(FilesPermissions.Upload);
}
