using Boilerplate.BuildingBlocks.Shared.Identity.Authorization;
using Boilerplate.BuildingBlocks.Web.Idempotency;
using Boilerplate.Modules.Tickets.Contracts.Authorization;
using Boilerplate.Modules.Tickets.Contracts.Dtos;
using Boilerplate.Modules.Tickets.Contracts.v1.Tickets;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Boilerplate.Modules.Tickets.Features.v1.Tickets.UpdateTicket;

public static class UpdateTicketEndpoint
{
    public sealed record UpdateTicketRequest(string Title, string? Description, TicketPriority Priority);

    internal static RouteHandlerBuilder MapUpdateTicketEndpoint(this IEndpointRouteBuilder endpoints)
    {
        return endpoints.MapPut("/tickets/{ticketId:guid}",
                async (Guid ticketId, UpdateTicketRequest body, IMediator mediator, CancellationToken ct) =>
                    Results.Ok(await mediator.Send(
                        new UpdateTicketCommand(ticketId, body.Title, body.Description, body.Priority), ct)))
            .WithName("UpdateTicket")
            .WithSummary("Edit a ticket's title, description, and priority")
            .RequirePermission(TicketsPermissions.Tickets.Update)
            .WithIdempotency();
    }
}
