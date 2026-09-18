using Boilerplate.BuildingBlocks.Shared.Identity.Authorization;
using Boilerplate.Modules.Tickets.Contracts.Authorization;
using Boilerplate.Modules.Tickets.Contracts.v1.Tickets;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Boilerplate.Modules.Tickets.Features.v1.Tickets.ListTicketComments;

public static class ListTicketCommentsEndpoint
{
    internal static RouteHandlerBuilder MapListTicketCommentsEndpoint(this IEndpointRouteBuilder endpoints)
    {
        return endpoints.MapGet("/tickets/{ticketId:guid}/comments",
                async (Guid ticketId, IMediator mediator, CancellationToken ct) =>
                    Results.Ok(await mediator.Send(new ListTicketCommentsQuery(ticketId), ct)))
            .WithName("ListTicketComments")
            .WithSummary("List the comments on a ticket")
            .RequirePermission(TicketsPermissions.Tickets.View);
    }
}
