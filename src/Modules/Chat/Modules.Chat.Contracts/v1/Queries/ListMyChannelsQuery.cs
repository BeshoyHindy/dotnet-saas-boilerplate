using System.Collections.ObjectModel;
using Boilerplate.Modules.Chat.Contracts.v1.DTOs;
using Mediator;

namespace Boilerplate.Modules.Chat.Contracts.v1.Queries;

public sealed record ListMyChannelsQuery(int Page = 1, int PageSize = 50)
    : IQuery<ReadOnlyCollection<ChannelDto>>;
