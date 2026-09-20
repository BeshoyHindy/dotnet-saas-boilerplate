using Boilerplate.Modules.Identity.Contracts.Authorization;
using Boilerplate.BuildingBlocks.Shared.Identity.Authorization;
using Boilerplate.Modules.Identity.Contracts.v1.Users.ChangePassword;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Boilerplate.Modules.Identity.Features.v1.Users.ChangePassword;

public static class ChangePasswordEndpoint
{
    internal static RouteHandlerBuilder MapChangePasswordEndpoint(this IEndpointRouteBuilder endpoints)
    {
        return endpoints.MapPost("/change-password", async (
            [FromBody] ChangePasswordCommand command,
            IMediator mediator,
            CancellationToken cancellationToken) =>
        {
            var result = await mediator.Send(command, cancellationToken);
            return TypedResults.Ok(result);
        })
        .WithName("ChangePassword")
        .WithSummary("Change password")
        .WithDescription("Change the current user's password.")
        .RequireAuthorization()
        // Self-service: changes the caller's own password after re-confirming the current one.
        .RequireAuthenticatedOnly()
        // Changes the subject's own credentials — an actor must not do this on their behalf.
        .DenyWhenActing()
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status403Forbidden);
    }
}