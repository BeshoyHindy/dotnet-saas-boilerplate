namespace Boilerplate.BuildingBlocks.Shared.Identity.Authorization;

/// <summary>
/// Checks whether a user holds a specific permission. Lets callers outside the
/// Identity module gate access without depending on Identity.Contracts.
/// </summary>
public interface IPermissionChecker
{
    /// <summary>
    /// Checks if a user has a specific permission.
    /// </summary>
    Task<bool> HasPermissionAsync(string userId, string permission, CancellationToken cancellationToken = default);
}
