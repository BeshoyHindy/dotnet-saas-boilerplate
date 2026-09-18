using Boilerplate.BuildingBlocks.Core.Domain;
using Boilerplate.Modules.Tickets.Contracts.Dtos;

namespace Boilerplate.Modules.Tickets.Domain.Events;

public sealed record TicketCreatedDomainEvent(
    Guid TicketId,
    string Number,
    string Title,
    TicketPriority Priority,
    Guid ReporterUserId,
    Guid? AssignedToUserId,
    Guid EventId,
    DateTimeOffset OccurredOnUtc) : DomainEvent(EventId, OccurredOnUtc);
