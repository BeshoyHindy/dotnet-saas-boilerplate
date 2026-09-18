using Boilerplate.BuildingBlocks.Core.Exceptions;
using Boilerplate.BuildingBlocks.Shared.Identity.Claims;
using Boilerplate.Modules.Identity.Contracts.v1.Users.GetUserPermissions;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using System.Security.Claims;

namespace Boilerplate.Modules.Identity.Features.v1.Users.GetUserPermissions;

public static class GetUserPermissionsEndpoint
{
    // No RequirePermission on purpose: returns the *caller's* own permissions (the SPA needs them to render
    // gated routes); gating behind Users.View would lock out non-user-managing roles. Fallback policy → 401.
    internal static RouteHandlerBuilder MapGetCurrentUserPermissionsEndpoint(this IEndpointRouteBuilder endpoints)
    {
        return endpoints.MapGet("/permissions", async (ClaimsPrincipal user, IMediator mediator, CancellationToken cancellationToken) =>
        {
            if (user.GetUserId() is not { } userId || string.IsNullOrEmpty(userId))
            {
                throw new UnauthorizedException();
            }

            return TypedResults.Ok(await mediator.Send(new GetCurrentUserPermissionsQuery(userId), cancellationToken));
        })
        .WithName("GetCurrentUserPermissions")
        .WithSummary("Get current user permissions")
        .WithDescription("Retrieve permissions for the authenticated user. Requires authentication only — every signed-in user can read their own grants.")
        .Produces<IEnumerable<string>>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized);
    }
}