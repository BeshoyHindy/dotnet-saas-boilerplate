using FluentValidation;
using Boilerplate.Modules.Identity.Contracts.v1.Roles.DeleteRole;

namespace Boilerplate.Modules.Identity.Features.v1.Roles.DeleteRole;

public sealed class DeleteRoleCommandValidator : AbstractValidator<DeleteRoleCommand>
{
    public DeleteRoleCommandValidator()
    {
        RuleFor(x => x.Id)
            .NotEmpty().WithMessage("Role ID is required.");
    }
}