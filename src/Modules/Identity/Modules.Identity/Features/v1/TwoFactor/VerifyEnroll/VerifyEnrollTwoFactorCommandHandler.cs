using Boilerplate.BuildingBlocks.Core.Context;
using Boilerplate.BuildingBlocks.Core.Exceptions;
using Boilerplate.Modules.Identity.Contracts.v1.TwoFactor;
using Boilerplate.Modules.Identity.Domain;
using Mediator;
using Microsoft.AspNetCore.Identity;

namespace Boilerplate.Modules.Identity.Features.v1.TwoFactor.VerifyEnroll;

public sealed class VerifyEnrollTwoFactorCommandHandler
    : ICommandHandler<VerifyEnrollTwoFactorCommand, bool>
{
    private readonly UserManager<AppUser> _userManager;
    private readonly ICurrentUser _currentUser;

    public VerifyEnrollTwoFactorCommandHandler(UserManager<AppUser> userManager, ICurrentUser currentUser)
    {
        _userManager = userManager;
        _currentUser = currentUser;
    }

    public async ValueTask<bool> Handle(
        VerifyEnrollTwoFactorCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!_currentUser.IsAuthenticated())
        {
            throw new UnauthorizedException();
        }

        var userId = _currentUser.GetUserId().ToString();
        var user = await _userManager.FindByIdAsync(userId)
            ?? throw new NotFoundException($"User {userId} not found.");

        var sanitized = command.Code.Replace(" ", string.Empty, StringComparison.Ordinal);
        var valid = await _userManager.VerifyTwoFactorTokenAsync(
            user,
            _userManager.Options.Tokens.AuthenticatorTokenProvider,
            sanitized);

        if (!valid)
        {
            throw new CustomException(
                "The authenticator code is invalid.",
                errors: null,
                System.Net.HttpStatusCode.BadRequest);
        }

        await _userManager.SetTwoFactorEnabledAsync(user, true);
        return true;
    }
}
