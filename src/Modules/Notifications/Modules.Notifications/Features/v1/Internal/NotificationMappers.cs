using Boilerplate.Modules.Notifications.Contracts.v1.DTOs;
using Boilerplate.Modules.Notifications.Domain;

namespace Boilerplate.Modules.Notifications.Features.v1.Internal;

internal static class NotificationMappers
{
    public static NotificationDto ToDto(this Notification n) =>
        new(n.Id, n.Type, n.Title, n.Body, n.Link, n.Source, n.MetadataJson, n.ReadAtUtc, n.CreatedAtUtc);
}
