using Mediator;

namespace Boilerplate.Modules.Chat.Contracts.v1.Commands;

public sealed record ArchiveChannelCommand(Guid ChannelId) : ICommand<Unit>;
