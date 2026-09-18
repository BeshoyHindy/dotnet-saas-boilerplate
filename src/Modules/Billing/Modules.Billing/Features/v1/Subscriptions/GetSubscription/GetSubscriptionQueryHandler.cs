using Finbuckle.MultiTenant.Abstractions;
using Boilerplate.BuildingBlocks.Core.Exceptions;
using Boilerplate.BuildingBlocks.Shared.Multitenancy;
using Boilerplate.Modules.Billing.Contracts.Dtos;
using Boilerplate.Modules.Billing.Contracts.v1.Subscriptions;
using Boilerplate.Modules.Billing.Data;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace Boilerplate.Modules.Billing.Features.v1.Subscriptions.GetSubscription;

public sealed class GetSubscriptionQueryHandler(
    BillingDbContext dbContext,
    IMultiTenantContextAccessor<AppTenantInfo> tenantAccessor)
    : IQueryHandler<GetSubscriptionQuery, SubscriptionDto?>
{
    public async ValueTask<SubscriptionDto?> Handle(GetSubscriptionQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var callerTenantId = tenantAccessor.MultiTenantContext?.TenantInfo?.Id
            ?? throw new UnauthorizedException("Tenant context is required.");

        // BillingDbContext is not tenant-filtered, so a tenant caller is pinned to its OWN
        // subscription and only root may pass an arbitrary tenant id (else cross-tenant reads).
        var tenantId = callerTenantId == MultitenancyConstants.Root.Id
            ? query.TenantId ?? callerTenantId
            : callerTenantId;

        var sub = await (from s in dbContext.Subscriptions.AsNoTracking()
                         join p in dbContext.Plans.AsNoTracking() on s.PlanId equals p.Id
                         where s.TenantId == tenantId
                            && s.Status == Contracts.SubscriptionStatus.Active
                         select new SubscriptionDto(s.Id, s.TenantId, s.PlanId, p.Key, s.StartUtc, s.EndUtc, s.Status))
                        .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return sub;
    }
}
