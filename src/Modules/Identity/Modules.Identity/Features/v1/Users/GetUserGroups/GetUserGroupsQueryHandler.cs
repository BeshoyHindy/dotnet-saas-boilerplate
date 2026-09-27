using Boilerplate.BuildingBlocks.Core.Exceptions;
using Boilerplate.BuildingBlocks.Persistence;
using Boilerplate.BuildingBlocks.Shared.Persistence;
using Boilerplate.Modules.Identity.Contracts.DTOs;
using Boilerplate.Modules.Identity.Contracts.v1.Users.GetUserGroups;
using Boilerplate.Modules.Identity.Data;
using Boilerplate.Modules.Identity.Features.v1.Groups;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace Boilerplate.Modules.Identity.Features.v1.Users.GetUserGroups;

public sealed class GetUserGroupsQueryHandler : IQueryHandler<GetUserGroupsQuery, PagedResponse<GroupDto>>
{
    private readonly IdentityDbContext _dbContext;

    public GetUserGroupsQueryHandler(IdentityDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async ValueTask<PagedResponse<GroupDto>> Handle(GetUserGroupsQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var userExists = await _dbContext.Users
            .AnyAsync(u => u.Id == query.UserId, cancellationToken)
            .ConfigureAwait(false);

        if (!userExists)
        {
            throw new NotFoundException($"User with ID '{query.UserId}' not found.");
        }

        // Id breaks ties so a page boundary never repeats or skips a group.
        var page = await _dbContext.Groups
            .AsNoTracking()
            .Include(g => g.GroupRoles)
            .Where(g => _dbContext.UserGroups.Any(ug => ug.UserId == query.UserId && ug.GroupId == g.Id))
            .OrderBy(g => g.Name)
            .ThenBy(g => g.Id)
            .ToPagedResponseAsync(query, cancellationToken)
            .ConfigureAwait(false);

        var items = await GroupDtoMapper.ToDtosAsync(_dbContext, page.Items, cancellationToken).ConfigureAwait(false);

        return new PagedResponse<GroupDto>
        {
            Items = items,
            PageNumber = page.PageNumber,
            PageSize = page.PageSize,
            TotalCount = page.TotalCount,
            TotalPages = page.TotalPages
        };
    }
}
