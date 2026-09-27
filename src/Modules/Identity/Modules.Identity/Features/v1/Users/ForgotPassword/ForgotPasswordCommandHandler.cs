using Boilerplate.BuildingBlocks.Shared.Multitenancy;
using Boilerplate.BuildingBlocks.Web.Origin;
using Boilerplate.Modules.Identity.Contracts.Services;
using Boilerplate.Modules.Identity.Contracts.v1.Users.ForgotPassword;
using Boilerplate.Modules.Identity.Services;
using Finbuckle.MultiTenant.Abstractions;
using Mediator;
using Microsoft.Extensions.Options;

namespace Boilerplate.Modules.Identity.Features.v1.Users.ForgotPassword;

public sealed class ForgotPasswordCommandHandler : ICommandHandler<ForgotPasswordCommand, string>
{
    private readonly IUserService _userService;
    private readonly IOptions<OriginOptions> _originOptions;
    private readonly IOptions<MailLinkOriginOptions> _mailLinkOriginOptions;
    private readonly IMultiTenantContextAccessor<AppTenantInfo> _multiTenantContextAccessor;

    public ForgotPasswordCommandHandler(
        IUserService userService,
        IOptions<OriginOptions> originOptions,
        IOptions<MailLinkOriginOptions> mailLinkOriginOptions,
        IMultiTenantContextAccessor<AppTenantInfo> multiTenantContextAccessor)
    {
        _userService = userService;
        _originOptions = originOptions;
        _mailLinkOriginOptions = mailLinkOriginOptions;
        _multiTenantContextAccessor = multiTenantContextAccessor;
    }

    public async ValueTask<string> Handle(ForgotPasswordCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var origin = MailLinkOrigin.Require(_originOptions, _mailLinkOriginOptions, _multiTenantContextAccessor);

        await _userService.ForgotPasswordAsync(command.Email, origin, cancellationToken).ConfigureAwait(false);

        return "Password reset email sent.";
    }
}