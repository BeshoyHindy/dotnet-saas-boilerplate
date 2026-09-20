using FluentValidation;
using Boilerplate.Modules.Identity.Contracts.v1.TwoFactor;

namespace Boilerplate.Modules.Identity.Features.v1.TwoFactor.Disable;

public sealed class DisableTwoFactorCommandValidator : AbstractValidator<DisableTwoFactorCommand>
{
    public DisableTwoFactorCommandValidator()
    {
        RuleFor(x => x.CurrentPassword).NotEmpty();
    }
}
