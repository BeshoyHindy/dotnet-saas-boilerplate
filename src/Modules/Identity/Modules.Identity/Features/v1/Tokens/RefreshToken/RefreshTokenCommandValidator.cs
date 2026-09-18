using FluentValidation;
using Boilerplate.Modules.Identity.Contracts.v1.Tokens.RefreshToken;

namespace Boilerplate.Modules.Identity.Features.v1.Tokens.RefreshToken;

public sealed class RefreshTokenCommandValidator : AbstractValidator<RefreshTokenCommand>
{
    public RefreshTokenCommandValidator()
    {
        // Token is intentionally not validated — see RefreshTokenCommand for why it's
        // optional. The handler cross-checks it only when present.
        RuleFor(p => p.RefreshToken)
            .Cascade(CascadeMode.Stop)
            .NotEmpty();
    }
}