using Boilerplate.BuildingBlocks.Shared.Persistence;
using Boilerplate.Modules.Identity.Contracts.DTOs;
using Mediator;

namespace Boilerplate.Modules.Identity.Contracts.v1.Users.GetUserGroups;

/// <summary>The groups one user belongs to, a page at a time, ordered by name.</summary>
public sealed class GetUserGroupsQuery : IPagedQuery, IQuery<PagedResponse<GroupDto>>
{
    /// <summary>The user, bound from the route.</summary>
    public string UserId { get; set; } = string.Empty;

    public int? PageNumber { get; set; }

    public int? PageSize { get; set; }

    /// <summary>Not honoured: groups are always ordered by name.</summary>
    public string? Sort { get; set; }
}
