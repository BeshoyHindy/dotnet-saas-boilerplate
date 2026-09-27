using Boilerplate.BuildingBlocks.Shared.Persistence;
using Boilerplate.Modules.Identity.Contracts.DTOs;
using Mediator;

namespace Boilerplate.Modules.Identity.Contracts.v1.Groups.GetGroupMembers;

/// <summary>One group's members, a page at a time, ordered by user name.</summary>
public sealed class GetGroupMembersQuery : IPagedQuery, IQuery<PagedResponse<GroupMemberDto>>
{
    /// <summary>The group, bound from the route.</summary>
    public Guid GroupId { get; set; }

    public int? PageNumber { get; set; }

    public int? PageSize { get; set; }

    /// <summary>Not honoured: members are always ordered by user name.</summary>
    public string? Sort { get; set; }
}
