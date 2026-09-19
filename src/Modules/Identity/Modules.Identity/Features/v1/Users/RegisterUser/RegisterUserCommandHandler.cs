using Boilerplate.BuildingBlocks.Web.Origin;
using Boilerplate.Modules.Identity.Contracts.Services;
using Boilerplate.Modules.Identity.Contracts.v1.Users.RegisterUser;
using Boilerplate.Modules.Identity.Services;
using Mediator;
using Microsoft.Extensions.Options;

namespace Boilerplate.Modules.Identity.Features.v1.Users.RegisterUser;

public sealed class RegisterUserCommandHandler : ICommandHandler<RegisterUserCommand, RegisterUserResponse>
{
    private readonly IUserService _userService;
    private readonly IOptions<OriginOptions> _originOptions;

    public RegisterUserCommandHandler(IUserService userService, IOptions<OriginOptions> originOptions)
    {
        _userService = userService;
        _originOptions = originOptions;
    }

    public async ValueTask<RegisterUserResponse> Handle(RegisterUserCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        // Same source as the password-reset link (see MailLinkOrigin): configuration, not the request.
        var origin = MailLinkOrigin.Require(_originOptions);

        string userId = await _userService.RegisterAsync(
            command.FirstName,
            command.LastName,
            command.Email,
            command.UserName,
            command.Password,
            command.ConfirmPassword,
            command.PhoneNumber ?? string.Empty,
            origin,
            cancellationToken).ConfigureAwait(false);

        return new RegisterUserResponse(userId);
    }
}
