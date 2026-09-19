using Boilerplate.BuildingBlocks.Web.Origin;
using Boilerplate.Modules.Identity.Contracts.Services;
using Boilerplate.Modules.Identity.Contracts.v1.Users.ResendConfirmationEmail;
using Boilerplate.Modules.Identity.Services;
using Mediator;
using Microsoft.Extensions.Options;

namespace Boilerplate.Modules.Identity.Features.v1.Users.ResendConfirmationEmail;

public sealed class ResendConfirmationEmailCommandHandler : ICommandHandler<ResendConfirmationEmailCommand, Unit>
{
    private readonly IUserService _userService;
    private readonly IOptions<OriginOptions> _originOptions;

    public ResendConfirmationEmailCommandHandler(IUserService userService, IOptions<OriginOptions> originOptions)
    {
        _userService = userService;
        _originOptions = originOptions;
    }

    public async ValueTask<Unit> Handle(ResendConfirmationEmailCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        // Same source as the password-reset link (see MailLinkOrigin): configuration, not the request.
        var origin = MailLinkOrigin.Require(_originOptions);

        await _userService.ResendConfirmationEmailAsync(command.UserId, origin, cancellationToken).ConfigureAwait(false);

        return Unit.Value;
    }
}
