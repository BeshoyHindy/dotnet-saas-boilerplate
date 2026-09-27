using Boilerplate.Modules.Identity.Contracts.Authorization;
using Boilerplate.BuildingBlocks.Shared.Identity.Authorization;
using Boilerplate.BuildingBlocks.Shared.Persistence;
using Boilerplate.Modules.Identity.Contracts.DTOs;
using Boilerplate.Modules.Identity.Contracts.v1.Groups.GetGroupMembers;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Boilerplate.Modules.Identity.Features.v1.Groups.GetGroupMembers;

public static class GetGroupMembersEndpoint
{
    public static RouteHandlerBuilder MapGetGroupMembersEndpoint(this IEndpointRouteBuilder endpoints)
    {
        return endpoints.MapGet("/groups/{groupId:guid}/members",
            async ([AsParameters] GetGroupMembersQuery query, IMediator mediator, CancellationToken cancellationToken) =>
                TypedResults.Ok(await mediator.Send(query, cancellationToken)))
        .WithName("GetGroupMembers")
        .WithSummary("Get members of a group (paged)")
        .RequirePermission(IdentityPermissions.Groups.View)
        .WithDescription("Retrieve the users that belong to a specific group, ordered by user name. Pageable via PageNumber/PageSize (at most 100).")
        .Produces<PagedResponse<GroupMemberDto>>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden)
        .Produces(StatusCodes.Status404NotFound);
    }
}
