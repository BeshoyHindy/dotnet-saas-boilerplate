using Boilerplate.BuildingBlocks.Eventing.Abstractions;
using Boilerplate.Modules.Billing.Contracts.v1.Plans;
using Boilerplate.Modules.Multitenancy.Contracts;
using Boilerplate.Modules.Multitenancy.Contracts.Events;
using Boilerplate.Modules.Multitenancy.Contracts.v1.RenewTenant;
using Mediator;
using Microsoft.Extensions.Options;

namespace Boilerplate.Modules.Multitenancy.Features.v1.RenewTenant;

public sealed class RenewTenantCommandHandler(
    ITenantService tenantService,
    IMediator mediator,
    IOutboxWriter outbox,
    IOptions<TenantBillingOptions> billingOptions,
    TimeProvider timeProvider)
    : ICommandHandler<RenewTenantCommand, RenewTenantCommandResponse>
{
    public async ValueTask<RenewTenantCommandResponse> Handle(RenewTenantCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        // Target plan: explicit key, else the tenant's current plan, else the configured default.
        var status = await tenantService.GetStatusAsync(command.TenantId, cancellationToken).ConfigureAwait(false);
        var targetKey = command.PlanKey;
        if (string.IsNullOrWhiteSpace(targetKey))
        {
            targetKey = string.IsNullOrWhiteSpace(status.Plan) ? billingOptions.Value.DefaultPlanKey : status.Plan!;
        }

        var term = await mediator.Send(new GetPlanTermQuery(targetKey!), cancellationToken).ConfigureAwait(false);

        var (periodStart, validUpto, planChanged) = await tenantService
            .RenewAsync(command.TenantId, term.Key, term.TermMonths, cancellationToken).ConfigureAwait(false);

        await outbox.AddAsync(new TenantRenewedIntegrationEvent(
            Id: Guid.NewGuid(),
            OccurredOnUtc: timeProvider.GetUtcNow().UtcDateTime,
            TenantId: command.TenantId,
            CorrelationId: Guid.NewGuid().ToString(),
            Source: "Multitenancy",
            PlanId: term.PlanId,
            PlanKey: term.Key,
            PeriodStartUtc: periodStart,
            PeriodEndUtc: validUpto,
            PlanChanged: planChanged), cancellationToken).ConfigureAwait(false);

        return new RenewTenantCommandResponse(command.TenantId, validUpto, term.Key, planChanged);
    }
}
