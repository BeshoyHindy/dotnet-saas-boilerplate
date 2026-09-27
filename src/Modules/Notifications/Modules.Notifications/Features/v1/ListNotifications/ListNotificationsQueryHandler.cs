using Boilerplate.BuildingBlocks.Core.Context;
using Boilerplate.BuildingBlocks.Core.Exceptions;
using Boilerplate.BuildingBlocks.Persistence;
using Boilerplate.BuildingBlocks.Shared.Persistence;
using Boilerplate.Modules.Notifications.Contracts.v1.DTOs;
using Boilerplate.Modules.Notifications.Contracts.v1.Queries;
using Boilerplate.Modules.Notifications.Data;
using Boilerplate.Modules.Notifications.Features.v1.Internal;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace Boilerplate.Modules.Notifications.Features.v1.ListNotifications;

public sealed class ListNotificationsQueryHandler(
    NotificationsDbContext db,
    ICurrentUser currentUser)
    : IQueryHandler<ListNotificationsQuery, PagedResponse<NotificationDto>>
{
    /// <summary>Page size when the caller names none; the shared pager's own default is smaller.</summary>
    public const int DefaultPageSize = 50;

    public async ValueTask<PagedResponse<NotificationDto>> Handle(ListNotificationsQuery q, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(q);
        var userId = currentUser.GetUserId();
        if (userId == Guid.Empty) throw new UnauthorizedException("no current user");
        var currentUserId = userId.ToString();

        q.PageSize ??= DefaultPageSize;

        var query = db.Notifications.AsNoTracking()
            .Where(n => n.UserId == currentUserId);

        if (q.UnreadOnly == true)
        {
            query = query.Where(n => n.ReadAtUtc == null);
        }

        // Id breaks CreatedAtUtc ties so a row never appears on two pages.
        return await query
            .OrderByDescending(n => n.CreatedAtUtc)
            .ThenByDescending(n => n.Id)
            .Select(NotificationMappers.ToDtoProjection)
            .ToPagedResponseAsync(q, cancellationToken)
            .ConfigureAwait(false);
    }
}
