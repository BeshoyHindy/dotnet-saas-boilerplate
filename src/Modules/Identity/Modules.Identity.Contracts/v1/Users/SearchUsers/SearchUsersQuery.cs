using Boilerplate.BuildingBlocks.Shared.Persistence;
using Boilerplate.Modules.Identity.Contracts.DTOs;
using Mediator;

namespace Boilerplate.Modules.Identity.Contracts.v1.Users.SearchUsers;

public sealed class SearchUsersQuery : IPagedQuery, IQuery<PagedResponse<UserDto>>
{
    public int? PageNumber { get; set; }

    public int? PageSize { get; set; }

    public string? Sort { get; set; }

    public string? Search { get; set; }

    public bool? IsActive { get; set; }

    public bool? EmailConfirmed { get; set; }

    public string? RoleId { get; set; }
}