using FluentValidation;
using Boilerplate.Modules.Chat.Contracts.v1.Commands;

namespace Boilerplate.Modules.Chat.Features.v1.Messages.PinMessage;

public sealed class PinMessageCommandValidator : AbstractValidator<PinMessageCommand>
{
    public PinMessageCommandValidator()
    {
        RuleFor(x => x.MessageId).NotEmpty();
    }
}
