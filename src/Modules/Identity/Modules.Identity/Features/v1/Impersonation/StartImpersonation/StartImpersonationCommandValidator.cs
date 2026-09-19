using FluentValidation;
using Boilerplate.Modules.Identity.Contracts.v1.Impersonation.StartImpersonation;
using Microsoft.Extensions.Options;

namespace Boilerplate.Modules.Identity.Features.v1.Impersonation.StartImpersonation;

public sealed class StartImpersonationCommandValidator : AbstractValidator<StartImpersonationCommand>
{
    /// <summary>
    /// Upper bound on impersonation token lifetime. There is exactly ONE such number in the system
    /// (<see cref="OperatorExchangeOptions.MaxMinutes"/>): the issuer clamps to it regardless, and
    /// this rule only bounces obviously abusive values (negative, zero, absurd) up front.
    /// </summary>
    public StartImpersonationCommandValidator(IOptions<OperatorExchangeOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var maxMinutes = options.Value.MaxMinutes;

        RuleFor(p => p.TargetUserId)
            .Cascade(CascadeMode.Stop)
            .NotEmpty();

        RuleFor(p => p.TargetTenantId)
            .Cascade(CascadeMode.Stop)
            .NotEmpty();

        RuleFor(p => p.DurationMinutes!.Value)
            .GreaterThan(0)
            .LessThanOrEqualTo(maxMinutes)
            .WithMessage($"Duration must be between 1 and {maxMinutes} minutes.")
            .When(p => p.DurationMinutes.HasValue);
    }
}
