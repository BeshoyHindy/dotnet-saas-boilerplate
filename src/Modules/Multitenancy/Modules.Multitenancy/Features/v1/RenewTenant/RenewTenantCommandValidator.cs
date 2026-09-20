using FluentValidation;
using Boilerplate.Modules.Multitenancy.Contracts.v1.RenewTenant;

namespace Boilerplate.Modules.Multitenancy.Features.v1.RenewTenant;

public sealed class RenewTenantCommandValidator : AbstractValidator<RenewTenantCommand>
{
    public RenewTenantCommandValidator()
    {
        RuleFor(t => t.TenantId).NotEmpty();

        // Optional — null falls back to the configured default term. 120 months (10 years) is the
        // upper bound, past which the caller almost certainly meant something else.
        RuleFor(t => t.Months)
            .InclusiveBetween(1, 120)
            .When(t => t.Months.HasValue)
            .WithMessage("Months must be between 1 and 120.");
    }
}
