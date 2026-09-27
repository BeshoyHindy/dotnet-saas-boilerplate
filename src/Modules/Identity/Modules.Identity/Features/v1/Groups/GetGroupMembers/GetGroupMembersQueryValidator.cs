using FluentValidation;
using Boilerplate.BuildingBlocks.Web.Validation;
using Boilerplate.Modules.Identity.Contracts.v1.Groups.GetGroupMembers;

namespace Boilerplate.Modules.Identity.Features.v1.Groups.GetGroupMembers;

public sealed class GetGroupMembersQueryValidator : AbstractValidator<GetGroupMembersQuery>
{
    public GetGroupMembersQueryValidator()
    {
        Include(new PagedQueryValidator<GetGroupMembersQuery>());

        RuleFor(q => q.GroupId).NotEmpty();
    }
}
