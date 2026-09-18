using Boilerplate.Modules.Identity.Contracts.Services;
using Boilerplate.Modules.Identity.Contracts.v1.Users.AdminConfirmEmail;
using Mediator;

namespace Boilerplate.Modules.Identity.Features.v1.Users.AdminConfirmEmail;

public sealed class AdminConfirmEmailCommandHandler : ICommandHandler<AdminConfirmEmailCommand, Unit>
{
    private readonly IUserService _userService;

    public AdminConfirmEmailCommandHandler(IUserService userService)
    {
        _userService = userService;
    }

    public async ValueTask<Unit> Handle(AdminConfirmEmailCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        await _userService.AdminConfirmEmailAsync(command.UserId, cancellationToken).ConfigureAwait(false);

        return Unit.Value;
    }
}
