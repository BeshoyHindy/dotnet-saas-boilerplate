using Boilerplate.BuildingBlocks.Shared.Identity.Authorization;

namespace Boilerplate.Modules.Identity.Contracts.Services;

/// <summary>
/// Service for user permission operations.
/// </summary>
public interface IUserPermissionService : IPermissionChecker
{
    /// <summary>
    /// Gets all permissions for a user.
    /// </summary>
    Task<List<string>?> GetPermissionsAsync(string userId, CancellationToken cancellationToken);

    /// <summary>
    /// Invalidates the permission cache for a user.
    /// </summary>
    Task InvalidatePermissionCacheAsync(string userId, CancellationToken cancellationToken);
}