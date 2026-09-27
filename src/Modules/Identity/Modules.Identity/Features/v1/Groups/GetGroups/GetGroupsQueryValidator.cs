using FluentValidation;
using Boilerplate.BuildingBlocks.Web.Validation;
using Boilerplate.Modules.Identity.Contracts.v1.Groups.GetGroups;

namespace Boilerplate.Modules.Identity.Features.v1.Groups.GetGroups;

public sealed class GetGroupsQueryValidator : AbstractValidator<GetGroupsQuery>
{
    public GetGroupsQueryValidator()
    {
        Include(new PagedQueryValidator<GetGroupsQuery>());

        RuleFor(q => q.Search)
            .MaximumLength(200)
            .When(q => !string.IsNullOrEmpty(q.Search));
    }
}
