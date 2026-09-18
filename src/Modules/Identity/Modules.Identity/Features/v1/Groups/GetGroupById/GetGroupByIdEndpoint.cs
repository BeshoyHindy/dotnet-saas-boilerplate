using Boilerplate.Modules.Identity.Contracts.Authorization;
using Boilerplate.BuildingBlocks.Shared.Identity.Authorization;
using Boilerplate.Modules.Identity.Contracts.DTOs;
using Boilerplate.Modules.Identity.Contracts.v1.Groups.GetGroupById;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Boilerplate.Modules.Identity.Features.v1.Groups.GetGroupById;

public static class GetGroupByIdEndpoint
{
    public static RouteHandlerBuilder MapGetGroupByIdEndpoint(this IEndpointRouteBuilder endpoints)
    {
        return endpoints.MapGet("/groups/{id:guid}", async (Guid id, IMediator mediator, CancellationToken cancellationToken) =>
            TypedResults.Ok(await mediator.Send(new GetGroupByIdQuery(id), cancellationToken)))
        .WithName("GetGroupById")
        .WithSummary("Get group by ID")
        .RequirePermission(IdentityPermissions.Groups.View)
        .WithDescription("Retrieve a specific group by its ID including roles and member count.")
        .Produces<GroupDto>(StatusCodes.Status200OK);
    }
}