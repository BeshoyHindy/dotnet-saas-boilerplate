using Boilerplate.BuildingBlocks.Core.Domain;

namespace Boilerplate.Modules.Files.Domain.Events;

public sealed record FileSoftDeletedDomainEvent(
    Guid FileAssetId,
    string ActorUserId,
    Guid EventId,
    DateTimeOffset OccurredOnUtc) : DomainEvent(EventId, OccurredOnUtc);
