using Boilerplate.Modules.Identity.Domain.Events;
using Mediator;
using Microsoft.Extensions.Logging;

namespace Boilerplate.Modules.Identity.Events;

/// <summary>
/// Handles the UserRegisteredEvent domain event.
///
/// **It must not publish an integration event.** It used to, and that was a second
/// <c>UserRegisteredIntegrationEvent</c> for every registration — a duplicate welcome mail, and now
/// it would be a duplicate confirmation mail too (#86). `UserRegistrationService` publishes that
/// event itself, inside the registration transaction, which is the only way the row can share the
/// sign-up's fate: a publish from here runs in <c>DomainEventsInterceptor</c>, which logs and
/// swallows handler failures, so a lost outbox write would leave a committed user nobody was ever
/// told about.
///
/// What is left is what every other Identity domain-event handler does — one log line, which is
/// also how <c>RecordRegistered</c> stays a fact the module records rather than a dead call.
/// </summary>
public sealed class UserRegisteredHandler(
    ILogger<UserRegisteredHandler> logger)
    : INotificationHandler<UserRegisteredEvent>
{
    public ValueTask Handle(UserRegisteredEvent notification, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notification);

        if (logger.IsEnabled(LogLevel.Information))
        {
            // PII minimization: log the pseudonymous UserId only, not the email address.
            logger.LogInformation("User registered: {UserId}", notification.UserId);
        }

        return ValueTask.CompletedTask;
    }
}
