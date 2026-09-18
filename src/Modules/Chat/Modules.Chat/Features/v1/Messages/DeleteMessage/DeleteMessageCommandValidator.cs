using FluentValidation;
using Boilerplate.Modules.Chat.Contracts.v1.Commands;

namespace Boilerplate.Modules.Chat.Features.v1.Messages.DeleteMessage;

public sealed class DeleteMessageCommandValidator : AbstractValidator<DeleteMessageCommand>
{
    public DeleteMessageCommandValidator()
    {
        RuleFor(x => x.MessageId).NotEmpty();
    }
}
