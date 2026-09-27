using Boilerplate.Modules.Identity.Contracts.Authorization;
using Boilerplate.BuildingBlocks.Shared.Identity.Authorization;
using Boilerplate.BuildingBlocks.Shared.Persistence;
using Boilerplate.Modules.Identity.Contracts.DTOs;
using Boilerplate.Modules.Identity.Contracts.v1.Users.GetUserGroups;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Boilerplate.Modules.Identity.Features.v1.Users.GetUserGroups;

public static class GetUserGroupsEndpoint
{
    public static RouteHandlerBuilder MapGetUserGroupsEndpoint(this IEndpointRouteBuilder endpoints)
    {
        return endpoints.MapGet("/users/{userId}/groups",
            async ([AsParameters] GetUserGroupsQuery query, IMediator mediator, CancellationToken cancellationToken) =>
                TypedResults.Ok(await mediator.Send(query, cancellationToken)))
        .WithName("GetUserGroups")
        .WithSummary("Get groups for a user (paged)")
        .RequirePermission(IdentityPermissions.Groups.View)
        .WithDescription("Retrieve the groups a specific user belongs to, ordered by name. Pageable via PageNumber/PageSize (at most 100).")
        .Produces<PagedResponse<GroupDto>>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden)
        .Produces(StatusCodes.Status404NotFound);
    }
}
