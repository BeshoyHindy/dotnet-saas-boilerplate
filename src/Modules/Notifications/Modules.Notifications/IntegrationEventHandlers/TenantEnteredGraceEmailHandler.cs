using Boilerplate.BuildingBlocks.Eventing.Abstractions;
using Boilerplate.BuildingBlocks.Mailing.Services;
using Boilerplate.Modules.Multitenancy.Contracts.Events;
using Microsoft.Extensions.Logging;

namespace Boilerplate.Modules.Notifications.IntegrationEventHandlers;

/// <summary>Emails the tenant admin that their account lapsed and the grace period is counting down.</summary>
public sealed class TenantEnteredGraceEmailHandler(
    IMailService mailService,
    ILogger<TenantEnteredGraceEmailHandler> logger)
    : IIntegrationEventHandler<TenantEnteredGraceIntegrationEvent>
{
    public async Task HandleAsync(TenantEnteredGraceIntegrationEvent @event, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(@event);
        var (subject, body) = TenantLifecycleEmailBodies.EnteredGrace(
            @event.TenantName, @event.ValidUpto, @event.GraceEndsUtc);
        await TenantLifecycleEmailSender.SendAsync(mailService, logger, @event.AdminEmail, subject, body, "entered-grace", ct)
            .ConfigureAwait(false);
    }
}
