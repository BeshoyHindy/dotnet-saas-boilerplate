using Mediator;

namespace Boilerplate.Modules.Chat.Contracts.v1.Commands;

public sealed record PinMessageCommand(Guid MessageId) : ICommand<Unit>;

public sealed record UnpinMessageCommand(Guid MessageId) : ICommand<Unit>;
