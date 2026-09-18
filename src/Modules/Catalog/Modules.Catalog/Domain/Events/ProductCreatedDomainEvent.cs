using Boilerplate.BuildingBlocks.Core.Domain;

namespace Boilerplate.Modules.Catalog.Domain.Events;

public sealed record ProductCreatedDomainEvent(
    Guid ProductId,
    string Sku,
    string Name,
    Guid EventId,
    DateTimeOffset OccurredOnUtc) : DomainEvent(EventId, OccurredOnUtc);
