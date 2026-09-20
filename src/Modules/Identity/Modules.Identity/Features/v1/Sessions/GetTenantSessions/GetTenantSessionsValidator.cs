using FluentValidation;
using Boilerplate.Modules.Identity.Contracts.v1.Sessions.GetTenantSessions;

namespace Boilerplate.Modules.Identity.Features.v1.Sessions.GetTenantSessions;

public sealed class GetTenantSessionsValidator : AbstractValidator<GetTenantSessionsQuery>
{
    public GetTenantSessionsValidator()
    {
        RuleFor(x => x.PageNumber)
            .GreaterThanOrEqualTo(1).WithMessage("Page number must be greater than or equal to 1.");

        RuleFor(x => x.PageSize)
            .GreaterThanOrEqualTo(1).WithMessage("Page size must be greater than or equal to 1.");
    }
}
