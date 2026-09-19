using Boilerplate.BuildingBlocks.Web.Origin;
using Boilerplate.Modules.Identity.Contracts.Services;
using Boilerplate.Modules.Identity.Contracts.v1.Users.ForgotPassword;
using Boilerplate.Modules.Identity.Services;
using Mediator;
using Microsoft.Extensions.Options;

namespace Boilerplate.Modules.Identity.Features.v1.Users.ForgotPassword;

public sealed class ForgotPasswordCommandHandler : ICommandHandler<ForgotPasswordCommand, string>
{
    private readonly IUserService _userService;
    private readonly IOptions<OriginOptions> _originOptions;

    public ForgotPasswordCommandHandler(IUserService userService, IOptions<OriginOptions> originOptions)
    {
        _userService = userService;
        _originOptions = originOptions;
    }

    public async ValueTask<string> Handle(ForgotPasswordCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var origin = MailLinkOrigin.Require(_originOptions);

        await _userService.ForgotPasswordAsync(command.Email, origin, cancellationToken).ConfigureAwait(false);

        return "Password reset email sent.";
    }
}