using Finbuckle.MultiTenant.Abstractions;
using Boilerplate.BuildingBlocks.Core.Exceptions;
using Boilerplate.BuildingBlocks.Shared.Multitenancy;
using Boilerplate.Modules.Billing.Contracts.v1.Wallets;
using Boilerplate.Modules.Billing.Data;
using Boilerplate.Modules.Billing.Services;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace Boilerplate.Modules.Billing.Features.v1.Wallets.ApproveTopupRequest;

public sealed class ApproveTopupRequestCommandHandler(
    BillingDbContext db,
    IBillingService billing,
    IMultiTenantContextAccessor<AppTenantInfo> tenantAccessor)
    : ICommandHandler<ApproveTopupRequestCommand, Guid>
{
    public async ValueTask<Guid> Handle(ApproveTopupRequestCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var callerTenantId = tenantAccessor.MultiTenantContext?.TenantInfo?.Id
            ?? throw new UnauthorizedException("Tenant context is required.");
        var isRoot = callerTenantId == MultitenancyConstants.Root.Id;

        var request = await db.TopupRequests
            .FirstOrDefaultAsync(r => r.Id == command.Id, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new NotFoundException($"Top-up request {command.Id} not found.");

        if (!isRoot && request.TenantId != callerTenantId)
        {
            throw new UnauthorizedException("You can only approve top-up requests for your own tenant.");
        }

        // For root, operate on the request's own tenant; for non-root, callerTenantId equals request.TenantId.
        var invoice = await billing.CreateTopupInvoiceAsync(request.TenantId, command.Id, cancellationToken)
            .ConfigureAwait(false);

        return invoice.Id;
    }
}
