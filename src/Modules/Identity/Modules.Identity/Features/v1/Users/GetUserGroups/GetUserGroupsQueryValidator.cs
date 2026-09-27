using FluentValidation;
using Boilerplate.BuildingBlocks.Web.Validation;
using Boilerplate.Modules.Identity.Contracts.v1.Users.GetUserGroups;

namespace Boilerplate.Modules.Identity.Features.v1.Users.GetUserGroups;

public sealed class GetUserGroupsQueryValidator : AbstractValidator<GetUserGroupsQuery>
{
    public GetUserGroupsQueryValidator()
    {
        Include(new PagedQueryValidator<GetUserGroupsQuery>());

        RuleFor(q => q.UserId)
            .NotEmpty()
            .MaximumLength(450);
    }
}
