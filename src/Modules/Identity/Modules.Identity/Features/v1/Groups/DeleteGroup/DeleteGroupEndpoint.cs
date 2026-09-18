using Boilerplate.Modules.Identity.Contracts.Authorization;
using Boilerplate.BuildingBlocks.Shared.Identity.Authorization;
using Boilerplate.Modules.Identity.Contracts.v1.Groups.DeleteGroup;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Boilerplate.Modules.Identity.Features.v1.Groups.DeleteGroup;

public static class DeleteGroupEndpoint
{
    public static RouteHandlerBuilder MapDeleteGroupEndpoint(this IEndpointRouteBuilder endpoints)
    {
        return endpoints.MapDelete("/groups/{id:guid}", async (Guid id, IMediator mediator, CancellationToken cancellationToken) =>
        {
            await mediator.Send(new DeleteGroupCommand(id), cancellationToken);
            return TypedResults.NoContent();
        })
        .WithName("DeleteGroup")
        .WithSummary("Delete a group")
        .RequirePermission(IdentityPermissions.Groups.Delete)
        .WithDescription("Soft delete a group. System groups cannot be deleted.")
        .Produces(StatusCodes.Status204NoContent)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden);
    }
}