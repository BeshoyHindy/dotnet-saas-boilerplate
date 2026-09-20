using Boilerplate.Modules.Multitenancy.Contracts;
using Boilerplate.Modules.Multitenancy.Contracts.v1.CreateTenant;
using Boilerplate.Modules.Multitenancy.Provisioning;
using Boilerplate.BuildingBlocks.Shared.Multitenancy;
using Mediator;
using Microsoft.Extensions.Options;

namespace Boilerplate.Modules.Multitenancy.Features.v1.CreateTenant;

public sealed class CreateTenantCommandHandler(
    ITenantService tenantService,
    ITenantProvisioningService provisioningService,
    ITenantInitialPasswordBuffer passwordBuffer,
    IOptions<TenantValidityOptions> validityOptions,
    TimeProvider timeProvider)
    : ICommandHandler<CreateTenantCommand, CreateTenantCommandResponse>
{
    public async ValueTask<CreateTenantCommandResponse> Handle(CreateTenantCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        // Validity window: the caller's explicit date, else the configured default term from now.
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var validUpto = command.ValidUpto ?? now.AddMonths(validityOptions.Value.DefaultValidityMonths);

        var tenantId = await tenantService.CreateAsync(
            command.Id,
            command.Name,
            command.AdminEmail,
            command.Issuer,
            validUpto,
            cancellationToken).ConfigureAwait(false);

        // Buffer the admin password for IdentityDbInitializer's background seed step,
        // storing it before StartAsync so the seed never runs ahead of the buffer.
        passwordBuffer.Store(tenantId, command.AdminPassword);

        var provisioning = await provisioningService.StartAsync(tenantId, cancellationToken).ConfigureAwait(false);

        return new CreateTenantCommandResponse(
            tenantId,
            provisioning.CorrelationId,
            provisioning.Status.ToString());
    }
}
