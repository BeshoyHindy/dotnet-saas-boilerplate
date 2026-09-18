using Boilerplate.Modules.Tickets.Contracts.Dtos;
using Mediator;

namespace Boilerplate.Modules.Tickets.Contracts.v1.Tickets;

public sealed record GetTicketByIdQuery(Guid TicketId) : IQuery<TicketDto>;
