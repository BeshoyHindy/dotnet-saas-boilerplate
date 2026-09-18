using Boilerplate.BuildingBlocks.Shared.Persistence;
using Boilerplate.Modules.Tickets.Contracts.Dtos;
using Mediator;

namespace Boilerplate.Modules.Tickets.Contracts.v1.Tickets;

public sealed record ListTrashedTicketsQuery(int PageNumber = 1, int PageSize = 20)
    : IQuery<PagedResponse<TicketDto>>;
