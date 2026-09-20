using Boilerplate.BuildingBlocks.Eventing.Abstractions;
using Boilerplate.BuildingBlocks.Mailing.Services;
using Boilerplate.BuildingBlocks.Web.Origin;
using Boilerplate.Modules.Identity.Contracts.Events;
using Boilerplate.Modules.Identity.Domain;
using Boilerplate.Modules.Identity.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Boilerplate.Modules.Identity.Events;

/// <summary>
/// Sends the confirmation link once a registration has actually committed (#86).
///
/// Registration used to queue this mail inline, mid-way through four separate commits, so a failure
/// after it still mailed a link to an account that then rolled back — or never rolled back and stayed
/// wedged. Now the mail hangs off the event, which shares the registration's transaction: no row, no
/// event, no mail. At-least-once delivery means a duplicate is possible and harmless (the link is the
/// same one); a lost mail on success is not, and that is what the outbox's retries buy.
///
/// The origin comes from configuration, exactly as it does in the request path
/// (<see cref="MailLinkOrigin"/>) — the dispatcher has no request to take a host from, and an
/// attacker-influenceable host in a confirmation link is an account-takeover primitive. So nothing
/// request-derived has to travel on the event, and the public contract stays as it was.
/// </summary>
internal sealed class UserRegisteredConfirmationMailHandler(
    UserManager<AppUser> userManager,
    ConfirmationMailBuilder mailBuilder,
    IMailService mailService,
    IOptions<OriginOptions> originOptions,
    ILogger<UserRegisteredConfirmationMailHandler> logger)
    : IIntegrationEventHandler<UserRegisteredIntegrationEvent>
{
    public async Task HandleAsync(UserRegisteredIntegrationEvent @event, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(@event);

        // The tenant is already installed by IEventTenantScope, so this read is tenant-filtered.
        var user = await userManager.FindByIdAsync(@event.UserId).ConfigureAwait(false);
        if (user is null)
        {
            // PII minimization: identify the recipient by UserId, not email address.
            logger.LogWarning(
                "No user {UserId} to send a registration confirmation to; skipping.", @event.UserId);
            return;
        }

        // Only a registration that still needs confirming gets one: an externally authenticated user
        // arrives already confirmed, and a redelivery after the user clicked the link is a no-op.
        if (user.EmailConfirmed)
        {
            return;
        }

        var mail = await mailBuilder
            .BuildAsync(user, MailLinkOrigin.Require(originOptions))
            .ConfigureAwait(false);

        if (mail is null)
        {
            return;
        }

        // Sent, not queued: this already runs on the dispatcher's cycle, and a throw here leaves the
        // outbox row unprocessed so the next cycle tries again. Queuing it would put the delivery
        // guarantee back in Hangfire's hands after the outbox had already taken it.
        await mailService.SendAsync(mail, ct).ConfigureAwait(false);
    }
}
