using FluentValidation;
using Boilerplate.Modules.Chat.Contracts.v1.Commands;

namespace Boilerplate.Modules.Chat.Features.v1.Reactions.RemoveReaction;

public sealed class RemoveReactionCommandValidator : AbstractValidator<RemoveReactionCommand>
{
    public RemoveReactionCommandValidator()
    {
        RuleFor(x => x.MessageId).NotEmpty();
        RuleFor(x => x.Emoji).NotEmpty().MaximumLength(64);
    }
}
