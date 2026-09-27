using Boilerplate.Modules.Identity.Contracts.DTOs;
using Boilerplate.Modules.Identity.Data;
using Boilerplate.Modules.Identity.Domain;
using Microsoft.EntityFrameworkCore;

namespace Boilerplate.Modules.Identity.Features.v1.Groups;

/// <summary>
/// Turns one page of groups (loaded with <see cref="Group.GroupRoles"/>) into <see cref="GroupDto"/>s,
/// with two batched lookups — member counts and role names — over that page only, never the tenant.
/// </summary>
internal static class GroupDtoMapper
{
    public static async Task<IReadOnlyCollection<GroupDto>> ToDtosAsync(
        IdentityDbContext dbContext,
        IReadOnlyCollection<Group> groups,
        CancellationToken cancellationToken)
    {
        if (groups.Count == 0)
        {
            return [];
        }

        var groupIds = groups.Select(g => g.Id).ToList();
        var memberCounts = await dbContext.UserGroups
            .AsNoTracking()
            .Where(ug => groupIds.Contains(ug.GroupId))
            .GroupBy(ug => ug.GroupId)
            .Select(g => new { GroupId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.GroupId, x => x.Count, cancellationToken)
            .ConfigureAwait(false);

        var roleIds = groups
            .SelectMany(g => g.GroupRoles.Select(gr => gr.RoleId))
            .Distinct()
            .ToList();

        var roleNames = roleIds.Count > 0
            ? await dbContext.Roles
                .AsNoTracking()
                .Where(r => roleIds.Contains(r.Id))
                .ToDictionaryAsync(r => r.Id, r => r.Name!, cancellationToken)
                .ConfigureAwait(false)
            : new Dictionary<string, string>();

        return groups.Select(g => new GroupDto
        {
            Id = g.Id,
            Name = g.Name,
            Description = g.Description,
            IsDefault = g.IsDefault,
            IsSystemGroup = g.IsSystemGroup,
            MemberCount = memberCounts.GetValueOrDefault(g.Id, 0),
            RoleIds = g.GroupRoles.Select(gr => gr.RoleId).ToList().AsReadOnly(),
            RoleNames = g.GroupRoles
                .Select(gr => roleNames.GetValueOrDefault(gr.RoleId, gr.RoleId))
                .ToList()
                .AsReadOnly(),
            CreatedAt = g.CreatedOnUtc
        }).ToList().AsReadOnly();
    }
}
