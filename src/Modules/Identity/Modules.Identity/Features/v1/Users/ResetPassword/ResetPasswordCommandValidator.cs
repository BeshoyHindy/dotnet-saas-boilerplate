using FluentValidation;
using Boilerplate.Modules.Identity.Contracts;
using Boilerplate.Modules.Identity.Contracts.v1.Users.ResetPassword;

namespace Boilerplate.Modules.Identity.Features.v1.Users.ResetPassword;

public sealed class ResetPasswordCommandValidator : AbstractValidator<ResetPasswordCommand>
{
    public ResetPasswordCommandValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress();
        RuleFor(x => x.Password)
            .NotEmpty()
            .MinimumLength(PasswordPolicy.MinimumLength)
            .WithMessage($"Password must be at least {PasswordPolicy.MinimumLength} characters.");
        RuleFor(x => x.Token).NotEmpty();
    }
}