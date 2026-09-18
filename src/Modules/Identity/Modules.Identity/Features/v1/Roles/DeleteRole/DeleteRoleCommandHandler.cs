using Boilerplate.Modules.Identity.Contracts.Services;
using Boilerplate.Modules.Identity.Contracts.v1.Roles.DeleteRole;
using Mediator;

namespace Boilerplate.Modules.Identity.Features.v1.Roles.DeleteRole;

public sealed class DeleteRoleCommandHandler : ICommandHandler<DeleteRoleCommand, Unit>
{
    private readonly IRoleService _roleService;

    public DeleteRoleCommandHandler(IRoleService roleService)
    {
        _roleService = roleService;
    }

    public async ValueTask<Unit> Handle(DeleteRoleCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        await _roleService.DeleteRoleAsync(command.Id, cancellationToken).ConfigureAwait(false);

        return Unit.Value;
    }
}