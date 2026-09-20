using FluentValidation;
using Boilerplate.Modules.Identity.Contracts.v1.Roles.GetRoles;

namespace Boilerplate.Modules.Identity.Features.v1.Roles.GetRoles;

public sealed class GetRolesQueryValidator : AbstractValidator<GetRolesQuery>
{
    public GetRolesQueryValidator()
    {
        RuleFor(x => x.PageNumber)
            .GreaterThanOrEqualTo(1).WithMessage("Page number must be greater than or equal to 1.");

        RuleFor(x => x.PageSize)
            .GreaterThanOrEqualTo(1).WithMessage("Page size must be greater than or equal to 1.");
    }
}
