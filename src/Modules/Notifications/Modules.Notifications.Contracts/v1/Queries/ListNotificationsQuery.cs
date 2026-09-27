using Boilerplate.BuildingBlocks.Shared.Persistence;
using Boilerplate.Modules.Notifications.Contracts.v1.DTOs;
using Mediator;

namespace Boilerplate.Modules.Notifications.Contracts.v1.Queries;

/// <summary>
/// Inbox list scoped to the caller, newest first. <see cref="UnreadOnly"/> filters to
/// <c>ReadAtUtc IS NULL</c>; otherwise the full mix is returned. <see cref="PageSize"/> defaults
/// to 50 when omitted. <see cref="Sort"/> is accepted for the shared paging contract but ignored:
/// an inbox is always newest first.
/// </summary>
public sealed class ListNotificationsQuery : IPagedQuery, IQuery<PagedResponse<NotificationDto>>
{
    public int? PageNumber { get; set; }

    public int? PageSize { get; set; }

    public string? Sort { get; set; }

    public bool? UnreadOnly { get; set; }
}
