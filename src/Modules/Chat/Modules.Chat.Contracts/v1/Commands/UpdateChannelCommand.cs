using Mediator;

namespace Boilerplate.Modules.Chat.Contracts.v1.Commands;

public sealed record UpdateChannelCommand(
    Guid ChannelId,
    string Name,
    string? Description,
    bool IsPrivate) : ICommand<Unit>;
