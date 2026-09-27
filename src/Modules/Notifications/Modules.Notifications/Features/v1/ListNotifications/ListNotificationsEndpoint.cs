using Boilerplate.BuildingBlocks.Shared.Identity.Authorization;
using Boilerplate.BuildingBlocks.Shared.Persistence;
using Boilerplate.Modules.Notifications.Contracts.Authorization;
using Boilerplate.Modules.Notifications.Contracts.v1.DTOs;
using Boilerplate.Modules.Notifications.Contracts.v1.Queries;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Boilerplate.Modules.Notifications.Features.v1.ListNotifications;

public static class ListNotificationsEndpoint
{
    internal static RouteHandlerBuilder MapListNotificationsEndpoint(this IEndpointRouteBuilder endpoints)
        => endpoints.MapGet("/",
                async ([AsParameters] ListNotificationsQuery query, IMediator mediator, CancellationToken cancellationToken) =>
                    TypedResults.Ok(await mediator.Send(query, cancellationToken)))
            .WithName("ListNotifications")
            .WithSummary("List the caller's notifications (newest first)")
            .RequirePermission(NotificationPermissions.Inbox.View)
            .Produces<PagedResponse<NotificationDto>>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);
}
