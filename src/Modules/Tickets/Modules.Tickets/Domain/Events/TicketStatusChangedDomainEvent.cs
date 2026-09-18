using Boilerplate.BuildingBlocks.Core.Domain;
using Boilerplate.Modules.Tickets.Contracts.Dtos;

namespace Boilerplate.Modules.Tickets.Domain.Events;

public sealed record TicketStatusChangedDomainEvent(
    Guid TicketId,
    TicketStatus PreviousStatus,
    TicketStatus NewStatus,
    Guid EventId,
    DateTimeOffset OccurredOnUtc) : DomainEvent(EventId, OccurredOnUtc);
