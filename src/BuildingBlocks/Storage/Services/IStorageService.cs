using Boilerplate.BuildingBlocks.Shared.Storage;
using Boilerplate.BuildingBlocks.Storage.DTOs;
using Boilerplate.BuildingBlocks.Storage.Keys;

namespace Boilerplate.BuildingBlocks.Storage.Services;

/// <summary>
/// <b>Object keys are tenant-prefixed by this block, never by caller convention</b> (ADR-0002).
/// Callers name an object with a tenant-<i>relative</i> path plus a <see cref="StorageSpace"/> —
/// through <see cref="ComposeKey"/> or <see cref="UploadAsync{T}"/> — and get back an opaque handle
/// they may persist. Nothing outside this block writes a tenant id or a key root; an architecture
/// test scans for it.
///
/// <para>Every method below that <i>takes</i> a stored handle refuses one the ambient tenant does
/// not own — <see cref="StorageKeyNotOwnedException"/> (404 on HTTP paths, so there is no existence
/// oracle) raised before any call reaches the backend. With no ambient tenant at all they throw
/// <see cref="MissingStorageTenantException"/>; there is no tenant-less key space to fall back to.
/// A handle may be given back as the key itself or as the URL a previous
/// <see cref="BuildPublicUrl"/>/<see cref="GenerateDownloadUrlAsync"/> returned — mapping that URL
/// back to a key is this block's job, not the caller's.</para>
/// </summary>
public interface IStorageService
{
    /// <summary>
    /// Writes <paramref name="request"/> into the <see cref="StorageSpace.Public"/> space as
    /// <c>uploads/tenants/{tenantId}/{typeName}/{guid}_{file}</c> and returns the durable unsigned
    /// URL for it (see <see cref="BuildPublicUrl"/>) — what <c>AppUser.ImageUrl</c> and
    /// <c>TenantTheme</c>'s brand-asset columns persist.
    /// </summary>
    Task<string> UploadAsync<T>(
        FileUploadRequest request,
        FileType fileType,
        CancellationToken cancellationToken = default) where T : class;

    /// <summary>
    /// Turns a tenant-relative path into the physical key for <paramref name="space"/>, prefixed
    /// with the ambient tenant. The result is opaque: persist it, hand it back to the methods here,
    /// and never parse or build one yourself.
    /// </summary>
    /// <exception cref="MissingStorageTenantException">There is no ambient tenant.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="relativePath"/> is empty, absolute, or contains a <c>.</c>/<c>..</c> segment,
    /// an empty segment, or any character outside <c>[A-Za-z0-9._-/]</c>.
    /// </exception>
    string ComposeKey(StorageSpace space, string relativePath);

    Task<FileDownloadResponse?> DownloadAsync(
        string path,
        CancellationToken cancellationToken = default);

    Task<bool> ExistsAsync(
        string path,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the size in bytes of the object at <paramref name="path"/>, or 0 if it does not exist.
    /// Lets callers account for storage usage on delete without having to track sizes themselves.
    /// </summary>
    Task<long> GetSizeAsync(string path, CancellationToken cancellationToken = default);

    Task RemoveAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// Best-effort counterpart to <see cref="RemoveAsync"/> for "replace this asset, then drop the
    /// one it replaced": deletes <paramref name="storedHandle"/> only when it is a key the ambient
    /// tenant owns, and otherwise <b>skips it and logs</b> instead of throwing. Returns whether a
    /// delete was attempted.
    ///
    /// <para>The handle being skipped is the realistic one: a development database written before
    /// keys carried a tenant still holds flat <c>uploads/{typeName}/…</c> values, and
    /// <c>PUT /identity/profile/image</c> lets a user store any URL at all. Neither should turn a
    /// profile save into a 500, and neither is ours to delete.</para>
    /// </summary>
    Task<bool> RemoveIfOwnedAsync(string? storedHandle, CancellationToken cancellationToken = default);

    /// <summary>
    /// Mint a short-lived presigned PUT URL the browser uses to upload bytes directly to S3-compatible storage.
    /// Returns the URL plus any headers the browser MUST include verbatim in its PUT (typically Content-Type
    /// when the signature constrains it). Used by the Files module's <c>RequestUploadUrl</c> endpoint.
    /// </summary>
    Task<PresignedUploadUrl> GenerateUploadUrlAsync(
        string storageKey,
        string contentType,
        long maxBytes,
        TimeSpan ttl,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Mint a short-lived presigned GET URL. When <paramref name="responseContentDisposition"/> is
    /// supplied, S3 echoes it in the download response so the browser surfaces the original filename
    /// rather than the storage key.
    /// </summary>
    Task<Uri> GenerateDownloadUrlAsync(
        string storageKey,
        TimeSpan ttl,
        string? responseContentDisposition = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// HEAD the object at <paramref name="storageKey"/>. Returns <c>null</c> when the object does not
    /// exist. The Files module's finalize handler uses this to verify size + content-type vs declared
    /// values before transitioning a row out of <c>PendingUpload</c>.
    /// </summary>
    Task<StoredObjectMetadata?> HeadObjectAsync(
        string storageKey,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Compute a durable, non-expiring public URL for an object. Valid <b>only for keys the bucket
    /// policy actually publishes</b> — i.e. the <c>uploads/</c> prefix written by
    /// <see cref="UploadAsync{T}"/> (avatars, tenant theme assets), which the deploy stacks grant
    /// anonymous read on. Use it when a long-lived persisted reference (e.g. an entity's
    /// <c>imageUrl</c> column) needs a URL that outlives a presign.
    ///
    /// <b>Not</b> for the Files module's <c>tenants/…</c> keys: that prefix carries public and
    /// private objects side by side and is deliberately never anonymously readable (granting it
    /// would publish every private file and make visibility changes unenforceable), so such a link
    /// 403s. Those go through <see cref="GenerateDownloadUrlAsync"/> with a short TTL.
    ///
    /// S3 backends build this from <c>PublicBaseUrl</c> (or the bucket's S3 host). Local storage
    /// returns a path relative to the API origin's wwwroot.
    /// </summary>
    /// <remarks>
    /// Returns <c>string</c> intentionally — local storage produces a server-relative path
    /// (resolved later by the client against the API origin) which is not a well-formed Uri.
    /// </remarks>
#pragma warning disable CA1055 // Uri vs string — see remarks above
    string BuildPublicUrl(string storageKey);
#pragma warning restore CA1055
}