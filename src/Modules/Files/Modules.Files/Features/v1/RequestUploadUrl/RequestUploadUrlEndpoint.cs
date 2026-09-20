using Boilerplate.BuildingBlocks.Shared.Identity.Authorization;
using Boilerplate.Modules.Files.Contracts.Authorization;
using Boilerplate.Modules.Files.Contracts.v1.Commands;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Boilerplate.Modules.Files.Features.v1.RequestUploadUrl;

public static class RequestUploadUrlEndpoint
{
    internal static RouteHandlerBuilder MapRequestUploadUrlEndpoint(this IEndpointRouteBuilder endpoints)
        => endpoints.MapPost("/upload-url",
                async (RequestUploadUrlCommand command, IMediator mediator, CancellationToken ct) =>
                    TypedResults.Ok(await mediator.Send(command, ct)))
            .WithName("RequestFileUploadUrl")
            .WithSummary("Mint a presigned PUT URL for a file upload")
            // Deliberately not idempotent: the response is a presigned URL that lives minutes while a
            // replay entry lives 24h, so a replay would hand back a dead link (#85). A repeated call
            // is harmless without it — it creates another pending FileAsset whose UploadDeadline
            // passes and which PurgeOrphanedFilesJob deletes. General rule: a response carrying a
            // short-lived capability must not be marked idempotent.
            .RequirePermission(FilesPermissions.Upload);
}
