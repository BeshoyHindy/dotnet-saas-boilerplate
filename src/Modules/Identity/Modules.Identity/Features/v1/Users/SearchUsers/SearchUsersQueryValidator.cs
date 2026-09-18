using FluentValidation;
using Boilerplate.BuildingBlocks.Web.Validation;
using Boilerplate.Modules.Identity.Contracts.v1.Users.SearchUsers;

namespace Boilerplate.Modules.Identity.Features.v1.Users.SearchUsers;

public sealed class SearchUsersQueryValidator : AbstractValidator<SearchUsersQuery>
{
    public SearchUsersQueryValidator()
    {
        Include(new PagedQueryValidator<SearchUsersQuery>());

        RuleFor(q => q.Search)
            .MaximumLength(200)
            .When(q => !string.IsNullOrEmpty(q.Search));

        RuleFor(q => q.RoleId)
            .MaximumLength(450)
            .When(q => !string.IsNullOrEmpty(q.RoleId));
    }
}