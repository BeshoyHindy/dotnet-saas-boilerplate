using FluentValidation;
using Boilerplate.Modules.Tickets.Contracts.v1.Tickets;

namespace Boilerplate.Modules.Tickets.Features.v1.Tickets.DeleteTicket;

public sealed class DeleteTicketCommandValidator : AbstractValidator<DeleteTicketCommand>
{
    public DeleteTicketCommandValidator()
    {
        RuleFor(x => x.TicketId).NotEmpty();
    }
}
