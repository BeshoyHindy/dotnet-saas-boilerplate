using Boilerplate.BuildingBlocks.Core.Exceptions;
using Boilerplate.Modules.Tickets.Contracts.v1.Tickets;
using Boilerplate.Modules.Tickets.Data;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace Boilerplate.Modules.Tickets.Features.v1.Tickets.DeleteTicket;

public sealed class DeleteTicketCommandHandler(TicketsDbContext dbContext)
    : ICommandHandler<DeleteTicketCommand, Unit>
{
    public async ValueTask<Unit> Handle(DeleteTicketCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var ticket = await dbContext.Tickets
            .FirstOrDefaultAsync(t => t.Id == command.TicketId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new NotFoundException($"Ticket {command.TicketId} not found.");

        // Soft delete: the audit interceptor converts the EF Delete into an IsDeleted flip.
        // Comments are not auto-included, so they are left untouched and survive a Restore.
        dbContext.Tickets.Remove(ticket);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Unit.Value;
    }
}
