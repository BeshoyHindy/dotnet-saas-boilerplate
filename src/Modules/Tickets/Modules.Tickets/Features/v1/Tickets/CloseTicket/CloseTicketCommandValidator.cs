using FluentValidation;
using Boilerplate.Modules.Tickets.Contracts.v1.Tickets;

namespace Boilerplate.Modules.Tickets.Features.v1.Tickets.CloseTicket;

public sealed class CloseTicketCommandValidator : AbstractValidator<CloseTicketCommand>
{
    public CloseTicketCommandValidator()
    {
        RuleFor(x => x.TicketId).NotEmpty();
    }
}
