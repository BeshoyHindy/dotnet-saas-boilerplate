using Boilerplate.BuildingBlocks.Core.Exceptions;
using Boilerplate.Modules.Tickets.Contracts.v1.Tickets;
using Boilerplate.Modules.Tickets.Data;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace Boilerplate.Modules.Tickets.Features.v1.Tickets.AssignTicket;

public sealed class AssignTicketCommandHandler(TicketsDbContext dbContext)
    : ICommandHandler<AssignTicketCommand, Guid>
{
    public async ValueTask<Guid> Handle(AssignTicketCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var ticket = await dbContext.Tickets
            .FirstOrDefaultAsync(t => t.Id == command.TicketId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new NotFoundException($"Ticket {command.TicketId} not found.");

        ticket.Assign(command.AssigneeUserId);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return ticket.Id;
    }
}
