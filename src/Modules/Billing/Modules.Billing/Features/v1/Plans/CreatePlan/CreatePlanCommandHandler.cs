using Boilerplate.Modules.Billing.Contracts.v1.Plans;
using Boilerplate.Modules.Billing.Data;
using Boilerplate.Modules.Billing.Domain;
using Mediator;

namespace Boilerplate.Modules.Billing.Features.v1.Plans.CreatePlan;

public sealed class CreatePlanCommandHandler(BillingDbContext dbContext)
    : ICommandHandler<CreatePlanCommand, Guid>
{
    public async ValueTask<Guid> Handle(CreatePlanCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var plan = BillingPlan.Create(command.Key, command.Name, command.Currency, command.MonthlyBasePrice,
            command.OverageRates, command.Interval, command.AnnualPrice);
        dbContext.Plans.Add(plan);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return plan.Id;
    }
}
