using Boilerplate.BuildingBlocks.Eventing.Abstractions;
using Boilerplate.BuildingBlocks.Mailing.Services;
using Boilerplate.Modules.Multitenancy.Contracts.Events;
using Microsoft.Extensions.Logging;

namespace Boilerplate.Modules.Notifications.IntegrationEventHandlers;

/// <summary>Emails the tenant admin that their subscription expired and access is suspended.</summary>
public sealed class TenantExpiredEmailHandler(
    IMailService mailService,
    ILogger<TenantExpiredEmailHandler> logger)
    : IIntegrationEventHandler<TenantExpiredIntegrationEvent>
{
    public async Task HandleAsync(TenantExpiredIntegrationEvent @event, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(@event);
        var (subject, body) = BillingEmailBodies.Expired(@event.TenantName, @event.PlanKey, @event.ValidUpto);
        await BillingEmailSender.SendAsync(mailService, logger, @event.AdminEmail, subject, body, "expired", ct)
            .ConfigureAwait(false);
    }
}
