using Boilerplate.BuildingBlocks.Core.Domain;
using Boilerplate.Modules.Files.Contracts.v1.DTOs;

namespace Boilerplate.Modules.Files.Domain.Events;

public sealed record FileFinalizedDomainEvent(
    Guid FileAssetId,
    string OwnerType,
    Guid? OwnerId,
    FileAssetStatus FinalStatus,
    Guid EventId,
    DateTimeOffset OccurredOnUtc) : DomainEvent(EventId, OccurredOnUtc);
