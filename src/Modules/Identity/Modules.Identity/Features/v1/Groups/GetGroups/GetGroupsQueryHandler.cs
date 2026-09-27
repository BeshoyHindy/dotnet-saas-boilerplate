using Boilerplate.BuildingBlocks.Persistence;
using Boilerplate.BuildingBlocks.Shared.Persistence;
using Boilerplate.Modules.Identity.Contracts.DTOs;
using Boilerplate.Modules.Identity.Contracts.v1.Groups.GetGroups;
using Boilerplate.Modules.Identity.Data;
using Boilerplate.Modules.Identity.Domain;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace Boilerplate.Modules.Identity.Features.v1.Groups.GetGroups;

public sealed class GetGroupsQueryHandler : IQueryHandler<GetGroupsQuery, PagedResponse<GroupDto>>
{
    private readonly IdentityDbContext _dbContext;

    public GetGroupsQueryHandler(IdentityDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async ValueTask<PagedResponse<GroupDto>> Handle(GetGroupsQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        IQueryable<Group> groupsQuery = _dbContext.Groups
            .AsNoTracking()
            .Include(g => g.GroupRoles);

        // ILIKE on the raw columns, which their pg_trgm GIN indexes serve (see ContainsPattern).
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            string pattern = ContainsPattern.For(query.Search);
            const string escape = ContainsPattern.EscapeCharacter;
            groupsQuery = groupsQuery.Where(g =>
                EF.Functions.ILike(g.Name, pattern, escape) ||
                (g.Description != null && EF.Functions.ILike(g.Description, pattern, escape)));
        }

        // Id breaks ties so a page boundary never repeats or skips a group.
        var page = await groupsQuery
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
