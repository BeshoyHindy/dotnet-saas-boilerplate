using Boilerplate.BuildingBlocks.Eventing.Abstractions;
using Boilerplate.Modules.Billing.Data;
using Boilerplate.Modules.Billing.Services;
using Boilerplate.Modules.Multitenancy.Contracts.Events;
using Microsoft.Extensions.Logging;

namespace Boilerplate.Modules.Billing.IntegrationEventHandlers;

/// <summary>
/// Reacts to a tenant being created + subscribed to a plan: starts the active subscription and issues
/// the term's subscription invoice. Invoice creation is idempotent (guarded by invoice number).
/// </summary>
public sealed class TenantSubscribedIntegrationEventHandler(
    BillingDbContext db,
    IBillingService billing,
    ILogger<TenantSubscribedIntegrationEventHandler> logger)
    : IIntegrationEventHandler<TenantSubscribedIntegrationEvent>
{
    public async Task HandleAsync(TenantSubscribedIntegrationEvent @event, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(@event);
        var tenantId = @event.TenantId
            ?? throw new InvalidOperationException("TenantSubscribedIntegrationEvent is missing TenantId.");

        await TenantSubscriptionMaintenance.ReplaceActiveSubscriptionAsync(
            db, tenantId, @event.PlanId, @event.PeriodStartUtc, @event.PeriodEndUtc, ct).ConfigureAwait(false);

        await billing.CreateSubscriptionInvoiceAsync(
            tenantId, @event.PlanId, @event.PeriodStartUtc, @event.PeriodEndUtc, ct).ConfigureAwait(false);

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "[Billing] tenant {TenantId} subscribed to plan {PlanKey}; term ends {End:o}",
                tenantId, @event.PlanKey, @event.PeriodEndUtc);
        }
    }
}
