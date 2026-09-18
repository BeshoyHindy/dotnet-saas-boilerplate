using Boilerplate.Modules.Chat.Contracts.v1.DTOs;
using Mediator;

namespace Boilerplate.Modules.Chat.Contracts.v1.Queries;

public sealed record GetChannelByIdQuery(Guid ChannelId) : IQuery<ChannelDto>;
