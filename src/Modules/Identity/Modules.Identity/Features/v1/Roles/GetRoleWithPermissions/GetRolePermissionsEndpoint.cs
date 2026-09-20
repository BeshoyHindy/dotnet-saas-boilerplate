using Boilerplate.Modules.Identity.Contracts.Authorization;
using Boilerplate.BuildingBlocks.Shared.Identity.Authorization;
using Boilerplate.Modules.Identity.Contracts.DTOs;
using Boilerplate.Modules.Identity.Contracts.v1.Roles.GetRoleWithPermissions;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Boilerplate.Modules.Identity.Features.v1.Roles.GetRoleWithPermissions;

public static class GetRolePermissionsEndpoint
{
    public static RouteHandlerBuilder MapGetRolePermissionsEndpoint(this IEndpointRouteBuilder endpoints)
    {
        return endpoints.MapGet("/{id:guid}/permissions", async (string id, IMediator mediator, CancellationToken cancellationToken) =>
            TypedResults.Ok(await mediator.Send(new GetRoleWithPermissionsQuery(id), cancellationToken)))
        .WithName("GetRolePermissions")
        .WithSummary("Get role permissions")
        .RequirePermission(IdentityPermissions.Roles.View)
        .WithDescription("Retrieve a role along with its assigned permissions.")
        .Produces<RoleDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden)
        .Produces(StatusCodes.Status404NotFound);
    }
}