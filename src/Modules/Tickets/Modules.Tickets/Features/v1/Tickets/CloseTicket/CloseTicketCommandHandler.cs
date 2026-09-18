using Boilerplate.BuildingBlocks.Core.Exceptions;
using Boilerplate.Modules.Tickets.Contracts.v1.Tickets;
using Boilerplate.Modules.Tickets.Data;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace Boilerplate.Modules.Tickets.Features.v1.Tickets.CloseTicket;

public sealed class CloseTicketCommandHandler(TicketsDbContext dbContext)
    : ICommandHandler<CloseTicketCommand, Guid>
{
    public async ValueTask<Guid> Handle(CloseTicketCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var ticket = await dbContext.Tickets
            .FirstOrDefaultAsync(t => t.Id == command.TicketId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new NotFoundException($"Ticket {command.TicketId} not found.");

        ticket.Close();
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return ticket.Id;
    }
}
