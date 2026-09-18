using Mediator;

namespace Boilerplate.Modules.Notifications.Contracts.v1.Commands;

/// <summary>Mark every unread notification for the caller as read. Returns the count updated.</summary>
public sealed record MarkAllNotificationsReadCommand : ICommand<int>;
