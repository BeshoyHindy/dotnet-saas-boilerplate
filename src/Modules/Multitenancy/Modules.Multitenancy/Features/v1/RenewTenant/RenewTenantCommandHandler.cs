using Boilerplate.Modules.Multitenancy.Contracts;
using Boilerplate.Modules.Multitenancy.Contracts.v1.RenewTenant;
using Mediator;
using Microsoft.Extensions.Options;

namespace Boilerplate.Modules.Multitenancy.Features.v1.RenewTenant;

public sealed class RenewTenantCommandHandler(
    ITenantService tenantService,
    IOptions<TenantValidityOptions> validityOptions)
    : ICommandHandler<RenewTenantCommand, RenewTenantCommandResponse>
{
    public async ValueTask<RenewTenantCommandResponse> Handle(RenewTenantCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var months = command.Months ?? validityOptions.Value.DefaultValidityMonths;

        var (_, validUpto) = await tenantService
            .RenewAsync(command.TenantId, months, cancellationToken).ConfigureAwait(false);

        return new RenewTenantCommandResponse(command.TenantId, validUpto);
    }
}
