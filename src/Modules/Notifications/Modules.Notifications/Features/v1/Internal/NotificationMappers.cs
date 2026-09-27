using System.Linq.Expressions;
using Boilerplate.Modules.Notifications.Contracts.v1.DTOs;
using Boilerplate.Modules.Notifications.Domain;

namespace Boilerplate.Modules.Notifications.Features.v1.Internal;

internal static class NotificationMappers
{
    /// <summary>Entity → DTO as an expression, so EF Core projects it in SQL (and can count the query).</summary>
    public static readonly Expression<Func<Notification, NotificationDto>> ToDtoProjection = n =>
        new NotificationDto(n.Id, n.Type, n.Title, n.Body, n.Link, n.Source, n.MetadataJson, n.ReadAtUtc, n.CreatedAtUtc);
}
