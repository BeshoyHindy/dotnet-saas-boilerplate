using Boilerplate.BuildingBlocks.Shared.Storage;
using Boilerplate.Modules.Identity.Contracts.DTOs;

namespace Boilerplate.Modules.Identity.Contracts.Services;

/// <summary>
/// Service for user profile operations.
/// </summary>
public interface IUserProfileService
{
    /// <summary>
    /// Gets a user by ID.
    /// </summary>
    Task<UserDto> GetAsync(string userId, CancellationToken cancellationToken);

    /// <summary>
    /// Gets the total user count.
    /// </summary>
    Task<int> GetCountAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Updates a user's profile, including the avatar.
    ///
    /// <para><b>An avatar arrives as bytes, never as a URL (#83).</b> <paramref name="image"/> is
    /// uploaded here and the column is set to what the Storage block hands back, so
    /// <c>AppUser.ImageUrl</c> only ever holds a value this server issued for this user. There is
    /// deliberately no "set my image URL" entry point: the one it replaced let a user name any
    /// string, including another user's avatar in the same tenant, which the next replace would then
    /// delete. <paramref name="deleteCurrentImage"/> is the removal path.</para>
    ///
    /// <para><b>Optimistic concurrency (#107).</b> <paramref name="ifMatch"/> is the caller's raw
    /// <c>If-Match</c> header, or null when it sent none. When present it is checked against the
    /// loaded row before anything else happens — before the avatar upload or delete — and a
    /// mismatch, like a <c>ConcurrencyFailure</c> from the save itself, is a 412.</para>
    /// </summary>
    Task UpdateAsync(string userId, string firstName, string lastName, string phoneNumber, FileUploadRequest image, bool deleteCurrentImage, string? ifMatch = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if a user exists with the given email.
    /// </summary>
    Task<bool> ExistsWithEmailAsync(string email, string? exceptId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if a user exists with the given username.
    /// </summary>
    Task<bool> ExistsWithNameAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if a user exists with the given phone number.
    /// </summary>
    Task<bool> ExistsWithPhoneNumberAsync(string phoneNumber, string? exceptId = null, CancellationToken cancellationToken = default);
}