using Boilerplate.BuildingBlocks.Shared.Persistence;
using Boilerplate.Modules.Identity.Contracts.DTOs;
using Mediator;

namespace Boilerplate.Modules.Identity.Contracts.v1.Groups.GetGroups;

/// <summary>The tenant's groups, a page at a time, ordered by name.</summary>
public sealed class GetGroupsQuery : IPagedQuery, IQuery<PagedResponse<GroupDto>>
{
    public int? PageNumber { get; set; }

    public int? PageSize { get; set; }

    /// <summary>Not honoured: groups are always ordered by name.</summary>
    public string? Sort { get; set; }

    /// <summary>Case-insensitive substring match against group name and description.</summary>
    public string? Search { get; set; }
}
