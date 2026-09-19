using Boilerplate.BuildingBlocks.Shared.Identity.Authorization;
using Boilerplate.BuildingBlocks.Shared.Multitenancy;
using Boilerplate.Modules.Multitenancy.Contracts.Authorization;
using Boilerplate.Modules.Multitenancy.Contracts.Dtos;
using Boilerplate.Modules.Multitenancy.Contracts.v1.UpdateTenantTheme;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Boilerplate.Modules.Multitenancy.Features.v1.UpdateTenantTheme;

public static class UpdateTenantThemeEndpoint
{
    public static RouteHandlerBuilder Map(IEndpointRouteBuilder endpoints)
    {
        return endpoints.MapPut("/theme", async (TenantThemeUpdateDto theme, IMediator mediator, CancellationToken cancellationToken) =>
            {
                await mediator.Send(new UpdateTenantThemeCommand(theme), cancellationToken);
                return TypedResults.NoContent();
            })
            .WithName("UpdateTenantTheme")
            .WithSummary("Update current tenant theme")
            .WithDescription("Update the theme settings for the current tenant: colors, typography, layout, and the brand assets. A brand asset is uploaded as bytes or removed with its delete flag — the asset URLs are response-only, so a client cannot point one at an object the server did not issue for that asset.")
            .RequirePermission(MultitenancyPermissions.Tenants.UpdateTheme)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);
    }
}