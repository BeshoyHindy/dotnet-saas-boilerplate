using Boilerplate.BuildingBlocks.Shared.Storage;
using Boilerplate.BuildingBlocks.Storage.DTOs;

namespace Boilerplate.BuildingBlocks.Storage.Services;

public interface IStorageService
{
    Task<string> UploadAsync<T>(
        FileUploadRequest request,
        FileType fileType,
        CancellationToken cancellationToken = default) where T : class;

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