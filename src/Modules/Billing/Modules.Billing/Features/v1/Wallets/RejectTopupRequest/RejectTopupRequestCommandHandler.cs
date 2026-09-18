using System.Net;
using Finbuckle.MultiTenant.Abstractions;
using Boilerplate.BuildingBlocks.Core.Exceptions;
using Boilerplate.BuildingBlocks.Shared.Multitenancy;
using Boilerplate.Modules.Billing.Contracts;
using Boilerplate.Modules.Billing.Contracts.v1.Wallets;
using Boilerplate.Modules.Billing.Data;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace Boilerplate.Modules.Billing.Features.v1.Wallets.RejectTopupRequest;

public sealed class RejectTopupRequestCommandHandler(
    BillingDbContext db,
    IMultiTenantContextAccessor<AppTenantInfo> tenantAccessor)
    : ICommandHandler<RejectTopupRequestCommand, Guid>
{
    public async ValueTask<Guid> Handle(RejectTopupRequestCommand command, CancellationToken cancellationToken)
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
            throw new UnauthorizedException("You can only reject top-up requests for your own tenant.");
        }

        if (request.Status != TopupRequestStatus.Pending)
        {
            throw new CustomException(
                $"Top-up request {command.Id} cannot be rejected because it is {request.Status} (only Pending requests can be rejected).",
                (IEnumerable<string>?)null,
                HttpStatusCode.Conflict);
        }

        request.Reject(command.Reason);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return request.Id;
    }
}
