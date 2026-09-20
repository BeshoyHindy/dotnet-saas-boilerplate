using FluentValidation;
using Boilerplate.Modules.Identity.Contracts.v1.TwoFactor;

namespace Boilerplate.Modules.Identity.Features.v1.TwoFactor.VerifyEnroll;

public sealed class VerifyEnrollTwoFactorCommandValidator : AbstractValidator<VerifyEnrollTwoFactorCommand>
{
    public VerifyEnrollTwoFactorCommandValidator()
    {
        RuleFor(x => x.Code)
            .NotEmpty()
            .MinimumLength(6)
            .MaximumLength(10); // allow spaces; handler strips them
    }
}
