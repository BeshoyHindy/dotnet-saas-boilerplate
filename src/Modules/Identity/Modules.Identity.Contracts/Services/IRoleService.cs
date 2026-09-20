using Boilerplate.BuildingBlocks.Shared.Persistence;
using Boilerplate.Modules.Identity.Contracts.DTOs;

namespace Boilerplate.Modules.Identity.Contracts.Services;

public interface IRoleService
{
    Task<PagedResponse<RoleDto>> GetRolesAsync(
        int pageNumber = 1,
        int pageSize = 20,
        string? search = null,
        CancellationToken cancellationToken = default);
    Task<RoleDto?> GetRoleAsync(string id, CancellationToken cancellationToken = default);
    Task<RoleDto> CreateOrUpdateRoleAsync(string roleId, string name, string description, CancellationToken cancellationToken = default);
    Task DeleteRoleAsync(string id, CancellationToken cancellationToken = default);
    Task<RoleDto> GetWithPermissionsAsync(string id, CancellationToken cancellationToken = default);
    Task<string> UpdatePermissionsAsync(string roleId, List<string> permissions, CancellationToken cancellationToken = default);
}