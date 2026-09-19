using Boilerplate.Modules.Identity.Contracts.v1.Operators.ExchangeOperatorToken;
using FluentValidation;

namespace Boilerplate.Modules.Identity.Features.v1.Operators.ExchangeOperatorToken;

/// <summary>
/// Shape only. The lifetime ceiling is NOT enforced here: a caller may ask for more than
/// <c>OperatorExchangeOptions.MaxMinutes</c> and the issuer clamps it, so operators never get a
/// 400 for asking for "a long session" — they get a short one. Absurd values are still rejected.
/// </summary>
public sealed class ExchangeOperatorTokenCommandValidator : AbstractValidator<ExchangeOperatorTokenCommand>
{
    public const int MaxRequestableMinutes = 1440;

    public ExchangeOperatorTokenCommandValidator()
    {
        RuleFor(p => p.TargetTenantId)
            .Cascade(CascadeMode.Stop)
            .NotEmpty()
            .MaximumLength(64);

        // Required and audited: every exchange has to say why it happened.
        RuleFor(p => p.Reason)
            .Cascade(CascadeMode.Stop)
            .NotEmpty()
            .MaximumLength(500);

        RuleFor(p => p.TargetUserId)
            .MaximumLength(64)
            .When(p => !string.IsNullOrEmpty(p.TargetUserId));

        RuleFor(p => p.DurationMinutes!.Value)
            .GreaterThan(0)
            .LessThanOrEqualTo(MaxRequestableMinutes)
            .WithMessage($"Duration must be between 1 and {MaxRequestableMinutes} minutes.")
            .When(p => p.DurationMinutes.HasValue);
    }
}
