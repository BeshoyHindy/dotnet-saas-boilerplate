using Boilerplate.BuildingBlocks.Core.Domain;
using Boilerplate.Modules.Chat.Contracts.v1.DTOs;

namespace Boilerplate.Modules.Chat.Domain.Events;

public sealed record ChannelCreatedDomainEvent(
    Guid ChannelId,
    ChannelType Type,
    string? Name,
    string CreatedByUserId,
    Guid EventId,
    DateTimeOffset OccurredOnUtc) : DomainEvent(EventId, OccurredOnUtc);
