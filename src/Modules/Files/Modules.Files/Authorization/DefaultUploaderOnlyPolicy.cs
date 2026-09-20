using Boilerplate.Modules.Files.Contracts;

namespace Boilerplate.Modules.Files.Authorization;

/// <summary>
/// Default policy used for the built-in <c>MyFiles</c> and <c>User</c> owner types.
/// - Attach: an authenticated user, to their own owner id or to none at all.
/// - Read: Public files visible to anyone in tenant; Private files only to the uploader.
/// - Delete: only the uploader.
///
/// Tenant scoping is handled by the framework's BaseDbContext (schema-per-tenant), not here.
/// Owning modules with different rules (e.g. participants-only) register their own
/// <see cref="IFileAccessPolicy"/> implementation that supersedes this one.
/// </summary>
internal sealed class DefaultUploaderOnlyPolicy : IFileAccessPolicy
{
    public DefaultUploaderOnlyPolicy(string ownerType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerType);
        OwnerType = ownerType;
    }

    public string OwnerType { get; }

    /// <summary>
    /// These owner types are self-owned: <c>MyFiles</c> carries no owner at all and <c>User</c>
    /// carries the uploader's own id, which is what the shipped profile screen sends. An owner id
    /// that is somebody else's was accepted before and produced a row only its uploader could ever
    /// read — and, given another tenant's user id, a file in this tenant referencing a subject of
    /// that one, created by a caller who cannot see whether that subject exists.
    ///
    /// The comparison is flat: any id that is not the caller's own is refused, whichever tenant it
    /// came from, so the refusal answers nothing about who exists. A module whose files belong to
    /// something other than their uploader registers its own <see cref="IFileAccessPolicy"/>.
    /// </summary>
    public Task<bool> CanAttachAsync(Guid? ownerId, string currentUserId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(currentUserId))
        {
            return Task.FromResult(false);
        }

        return Task.FromResult(
            ownerId is null
            || (Guid.TryParse(currentUserId, out var caller) && ownerId.Value == caller));
    }

    public Task<bool> CanReadAsync(FileAccessContext context, string currentUserId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrEmpty(currentUserId)) return Task.FromResult(false);

        // Visibility 0 = Public, visible to anyone in the tenant.
        if (context.Visibility == 0) return Task.FromResult(true);
        return Task.FromResult(IsUploader(context, currentUserId));
    }

    public Task<bool> CanDeleteAsync(FileAccessContext context, string currentUserId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrEmpty(currentUserId)) return Task.FromResult(false);
        return Task.FromResult(IsUploader(context, currentUserId));
    }

    private static bool IsUploader(FileAccessContext context, string currentUserId)
        => string.Equals(currentUserId, context.CreatedByUserId, StringComparison.Ordinal);
}
