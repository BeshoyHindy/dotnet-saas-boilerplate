using Boilerplate.BuildingBlocks.Core.Exceptions;
using Boilerplate.BuildingBlocks.Persistence;
using Boilerplate.BuildingBlocks.Shared.Persistence;
using Boilerplate.Modules.Identity.Contracts.DTOs;
using Boilerplate.Modules.Identity.Contracts.v1.Groups.GetGroupMembers;
using Boilerplate.Modules.Identity.Data;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace Boilerplate.Modules.Identity.Features.v1.Groups.GetGroupMembers;

public sealed class GetGroupMembersQueryHandler : IQueryHandler<GetGroupMembersQuery, PagedResponse<GroupMemberDto>>
{
    private readonly IdentityDbContext _dbContext;

    public GetGroupMembersQueryHandler(IdentityDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async ValueTask<PagedResponse<GroupMemberDto>> Handle(GetGroupMembersQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var groupExists = await _dbContext.Groups
            .AnyAsync(g => g.Id == query.GroupId, cancellationToken)
            .ConfigureAwait(false);

        if (!groupExists)
        {
            throw new NotFoundException($"Group with ID '{query.GroupId}' not found.");
        }

        // UserId breaks ties so a page boundary never repeats or skips a member.
        return await _dbContext.UserGroups
            .AsNoTracking()
            .Where(ug => ug.GroupId == query.GroupId)
            .Join(
                _dbContext.Users,
                ug => ug.UserId,
                u => u.Id,
                (ug, u) => new GroupMemberDto
                {
                    UserId = u.Id,
                    UserName = u.UserName,
                    Email = u.Email,
                    FirstName = u.FirstName,
                    LastName = u.LastName,
                    AddedAt = ug.AddedAt,
                    AddedBy = ug.AddedBy
                })
            .OrderBy(m => m.UserName)
            .ThenBy(m => m.UserId)
            .ToPagedResponseAsync(query, cancellationToken)
            .ConfigureAwait(false);
    }
}
