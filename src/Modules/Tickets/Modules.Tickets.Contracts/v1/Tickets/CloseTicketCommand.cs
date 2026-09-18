using Mediator;

namespace Boilerplate.Modules.Tickets.Contracts.v1.Tickets;

public sealed record CloseTicketCommand(Guid TicketId) : ICommand<Guid>;
