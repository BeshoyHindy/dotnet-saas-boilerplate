using Amazon.S3;
using Amazon.S3.Model;
using Boilerplate.BuildingBlocks.Shared.Storage;
using Boilerplate.BuildingBlocks.Storage.DTOs;
using Boilerplate.BuildingBlocks.Storage.Keys;
using Boilerplate.BuildingBlocks.Storage.Services;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text.RegularExpressions;

namespace Boilerplate.BuildingBlocks.Storage.S3;

internal sealed partial class S3StorageService : IStorageService
{
    private readonly IAmazonS3 _s3;
    private readonly S3StorageOptions _options;
    private readonly ITenantStorageKeys _keys;
    private readonly ILogger<S3StorageService> _logger;
    private readonly FileExtensionContentTypeProvider _contentTypeProvider;

    // Source-generated, compiled once — the inline Regex.Replace calls re-parsed the pattern on every upload.
    [GeneratedRegex("[^a-z0-9]")]
    private static partial Regex FolderSanitizer();

    [GeneratedRegex(@"[^a-zA-Z0-9_\.-]")]
    private static partial Regex FileNameSanitizer();

    public S3StorageService(
        IAmazonS3 s3,
        IOptions<S3StorageOptions> options,
        ITenantStorageKeys keys,
        ILogger<S3StorageService> logger)
    {
        _s3 = s3;
        _options = options.Value ?? throw new ArgumentNullException(nameof(options));
        _keys = keys;
        _logger = logger;
        _contentTypeProvider = new FileExtensionContentTypeProvider();

        if (string.IsNullOrWhiteSpace(_options.Bucket))
        {
            throw new InvalidOperationException("Storage:S3:Bucket is required when using S3 storage.");
        }

        RejectIfKeyRoot(_options.Bucket, "Storage:S3:Bucket");
        RejectIfKeyRoot(_options.Prefix, "Storage:S3:Prefix");
    }

    /// <summary>
    /// <see cref="ToLogicalKey"/> strips the bucket and the deployment prefix off the front of a
    /// path before checking it against the key grammar. A bucket, or a prefix whose first segment,
    /// named the same as one of <see cref="TenantStorageKeyRules.KeyRootSegments"/> would make that
    /// stripping eat the key's own first segment instead — silently mapping every URL to the wrong
    /// key (#78 hardening item 2). Refused at construction, not discovered at the first delete.
    /// </summary>
    private static void RejectIfKeyRoot(string? value, string settingName)
    {
        var firstSegment = value?.Trim('/').Split('/', 2)[0];

        if (string.IsNullOrEmpty(firstSegment)
            || !TenantStorageKeyRules.KeyRootSegments.Contains(firstSegment, StringComparer.Ordinal))
        {
            return;
        }

        throw new InvalidOperationException(
            $"{settingName} '{value}' collides with a Storage key root " +
            $"({string.Join(", ", TenantStorageKeyRules.KeyRootSegments)}); choose a different value, " +
            "or a URL's bucket/prefix segment would be stripped as if it were the key's own first segment.");
    }

    public async Task<string> UploadAsync<T>(FileUploadRequest request, FileType fileType, CancellationToken cancellationToken = default) where T : class
    {
        ArgumentNullException.ThrowIfNull(request);

        var rules = FileTypeMetadata.GetRules(fileType);
        var extension = Path.GetExtension(request.FileName);

        if (string.IsNullOrWhiteSpace(extension) || !rules.AllowedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"File type '{extension}' is not allowed. Allowed: {string.Join(", ", rules.AllowedExtensions)}");
        }

        if (request.Data.Count > rules.MaxSizeInMB * 1024 * 1024)
        {
            throw new InvalidOperationException($"File exceeds max size of {rules.MaxSizeInMB} MB.");
        }

        var key = BuildKey<T>(SanitizeFileName(request.FileName));

        using var stream = new MemoryStream([.. request.Data]);

        var putRequest = new PutObjectRequest
        {
            BucketName = _options.Bucket,
            Key = ToPhysicalKey(key),
            InputStream = stream,
            // Never the client-supplied ContentType — see FileTypeMetadata.ContentTypeFor.
            ContentType = FileTypeMetadata.ContentTypeFor(extension)
        };

        // Rely on bucket policy for public access; do not set ACLs to avoid conflicts with ACL-disabled buckets.
        await _s3.PutObjectAsync(putRequest, cancellationToken).ConfigureAwait(false);
        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("Uploaded file to S3 bucket {Bucket} with key {Key}", _options.Bucket, key);
        }

        return BuildPublicUrl(key);
    }

    public async Task RemoveAsync(string path, CancellationToken cancellationToken = default)
    {
        var key = AuthorizeHandle(path);
        await RemoveAuthorizedAsync(key, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> RemoveIfOwnedAsync(string? storedHandle, CancellationToken cancellationToken = default)
    {
        if (!TryAuthorizeHandle(storedHandle, out var key))
        {
            return false;
        }

        await RemoveAuthorizedAsync(key, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Deletes an already-authorized logical <paramref name="key"/>. Both public delete entry
    /// points route here instead of calling each other, so <see cref="ToLogicalKey"/> — which
    /// strips the bucket and deployment prefix — runs exactly once per call (#78 hardening item 3;
    /// calling the public <see cref="RemoveAsync"/> from <see cref="RemoveIfOwnedAsync"/> would
    /// strip <c>Storage:S3:Prefix</c> a second time and delete the wrong physical object).
    /// </summary>
    private async Task RemoveAuthorizedAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            await _s3.DeleteObjectAsync(_options.Bucket, ToPhysicalKey(key), cancellationToken).ConfigureAwait(false);
        }
        catch (AmazonS3Exception ex)
        {
            _logger.LogWarning(ex, "S3 error deleting object {Key}: {StatusCode}", key, ex.StatusCode);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unexpected error deleting S3 object {Key}", key);
        }
    }

    public async Task<FileDownloadResponse?> DownloadAsync(string path, CancellationToken cancellationToken = default)
    {
        var key = AuthorizeHandle(path);

        try
        {
            var request = new GetObjectRequest
            {
                BucketName = _options.Bucket,
                Key = ToPhysicalKey(key)
            };

            var response = await _s3.GetObjectAsync(request, cancellationToken).ConfigureAwait(false);
            var fileName = Path.GetFileName(key);

            // Use response ContentType if available, otherwise determine from extension
            var contentType = response.Headers.ContentType;
            if (string.IsNullOrWhiteSpace(contentType) && !_contentTypeProvider.TryGetContentType(fileName, out contentType))
            {
                contentType = "application/octet-stream";
            }

            return new FileDownloadResponse
            {
                Stream = response.ResponseStream,
                ContentType = contentType!,
                FileName = fileName,
                ContentLength = response.ContentLength
            };
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "S3 object not found: {Key}", key);
            }
            return null;
        }
        catch (AmazonS3Exception ex)
        {
            _logger.LogWarning(ex, "S3 error downloading object {Key}: {StatusCode}", key, ex.StatusCode);
            return null;
        }
        // Fallback for unexpected non-S3 errors (e.g., network, serialization).
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unexpected error downloading S3 object {Key}", key);
            return null;
        }
    }

    public async Task<long> GetSizeAsync(string path, CancellationToken cancellationToken = default)
    {
        var key = AuthorizeHandle(path);

        try
        {
            var metadata = await _s3.GetObjectMetadataAsync(new GetObjectMetadataRequest
            {
                BucketName = _options.Bucket,
                Key = ToPhysicalKey(key)
            }, cancellationToken).ConfigureAwait(false);

            return metadata.ContentLength;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return 0;
        }
        catch (AmazonS3Exception ex)
        {
            _logger.LogWarning(ex, "S3 error reading object size {Key}: {StatusCode}", key, ex.StatusCode);
            return 0;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unexpected error reading S3 object size: {Key}", key);
            return 0;
        }
    }

    public async Task<bool> ExistsAsync(string path, CancellationToken cancellationToken = default)
    {
        var key = AuthorizeHandle(path);

        try
        {
            var request = new GetObjectMetadataRequest
            {
                BucketName = _options.Bucket,
                Key = ToPhysicalKey(key)
            };

            await _s3.GetObjectMetadataAsync(request, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }
        catch (AmazonS3Exception ex)
        {
            _logger.LogWarning(ex, "S3 error checking object existence {Key}: {StatusCode}", key, ex.StatusCode);
            return false;
        }
        // Fallback for unexpected non-S3 errors (e.g., network, configuration).
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unexpected error checking if S3 object exists: {Key}", key);
            return false;
        }
    }

    public async Task<PresignedUploadUrl> GenerateUploadUrlAsync(
        string storageKey,
        string contentType,
        long maxBytes,
        TimeSpan ttl,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);

        var key = ToPhysicalKey(AuthorizeHandle(storageKey));
        var expiresAt = DateTimeOffset.UtcNow.Add(ttl);

        var request = new GetPreSignedUrlRequest
        {
            BucketName = _options.Bucket,
            Key = key,
            Verb = HttpVerb.PUT,
            Expires = expiresAt.UtcDateTime,
            ContentType = contentType,
            Protocol = ResolvePresignProtocol()
        };

        var url = await _s3.GetPreSignedURLAsync(request).ConfigureAwait(false);

        var requiredHeaders = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Content-Type"] = contentType
        };

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("Issued presigned PUT URL for bucket {Bucket} key {Key} expires {ExpiresAt}",
                _options.Bucket, key, expiresAt);
        }

        return new PresignedUploadUrl(new Uri(url), requiredHeaders, expiresAt);
    }

    public async Task<Uri> GenerateDownloadUrlAsync(
        string storageKey,
        TimeSpan ttl,
        string? responseContentDisposition = null,
        CancellationToken cancellationToken = default)
    {
        var key = ToPhysicalKey(AuthorizeHandle(storageKey));

        var request = new GetPreSignedUrlRequest
        {
            BucketName = _options.Bucket,
            Key = key,
            Verb = HttpVerb.GET,
            Expires = DateTime.UtcNow.Add(ttl),
            Protocol = ResolvePresignProtocol()
        };

        if (!string.IsNullOrWhiteSpace(responseContentDisposition))
        {
            request.ResponseHeaderOverrides.ContentDisposition = responseContentDisposition;
        }

        var url = await _s3.GetPreSignedURLAsync(request).ConfigureAwait(false);
        return new Uri(url);
    }

    public async Task<StoredObjectMetadata?> HeadObjectAsync(
        string storageKey,
        CancellationToken cancellationToken = default)
    {
        var key = AuthorizeHandle(storageKey);

        try
        {
            var metadata = await _s3.GetObjectMetadataAsync(new GetObjectMetadataRequest
            {
                BucketName = _options.Bucket,
                Key = ToPhysicalKey(key)
            }, cancellationToken).ConfigureAwait(false);

            var contentType = string.IsNullOrWhiteSpace(metadata.Headers.ContentType)
                ? "application/octet-stream"
                : metadata.Headers.ContentType;

            var lastModified = metadata.LastModified.HasValue
                ? new DateTimeOffset(DateTime.SpecifyKind(metadata.LastModified.Value, DateTimeKind.Utc), TimeSpan.Zero)
                : DateTimeOffset.UtcNow;

            return new StoredObjectMetadata(
                metadata.ContentLength,
                contentType,
                lastModified,
                metadata.ETag);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
        catch (AmazonS3Exception ex)
        {
            _logger.LogWarning(ex, "S3 HEAD failed for {Key}: {StatusCode}", key, ex.StatusCode);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unexpected error on S3 HEAD for {Key}", key);
            return null;
        }
    }

    // MinIO and other S3-compatibles often serve plain HTTP, but the SDK defaults presigned URLs to
    // HTTPS regardless of ServiceURL scheme (un-PUTable there). Infer protocol from ServiceURL.
    private Protocol ResolvePresignProtocol()
    {
        if (!string.IsNullOrWhiteSpace(_options.ServiceUrl)
            && Uri.TryCreate(_options.ServiceUrl, UriKind.Absolute, out var uri)
            && string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            return Protocol.HTTP;
        }
        return Protocol.HTTPS;
    }

    public string ComposeKey(StorageSpace space, string relativePath) => _keys.Compose(space, relativePath);

    private string BuildKey<T>(string fileName) where T : class
    {
        var folder = FolderSanitizer().Replace(typeof(T).Name.ToLowerInvariant(), "_");
        return _keys.Compose(StorageSpace.Public, $"{folder}/{Guid.NewGuid():N}_{fileName}");
    }

    public string BuildPublicUrl(string storageKey)
    {
        var key = ToPhysicalKey(AuthorizeHandle(storageKey));

        if (!string.IsNullOrWhiteSpace(_options.PublicBaseUrl))
        {
            return $"{_options.PublicBaseUrl.TrimEnd('/')}/{key}";
        }

        // S3-compatible endpoint with path-style addressing (MinIO and friends).
        if (!string.IsNullOrWhiteSpace(_options.ServiceUrl))
        {
            return $"{_options.ServiceUrl.TrimEnd('/')}/{_options.Bucket}/{key}";
        }

        if (!_options.PublicRead)
        {
            return key;
        }

        if (string.IsNullOrWhiteSpace(_options.Region) || string.Equals(_options.Region, "us-east-1", StringComparison.OrdinalIgnoreCase))
        {
            return $"https://{_options.Bucket}.s3.amazonaws.com/{key}";
        }

        return $"https://{_options.Bucket}.s3.{_options.Region}.amazonaws.com/{key}";
    }

    /// <summary>
    /// Maps a persisted handle back to a logical key and proves the ambient tenant owns it.
    /// Throws <see cref="StorageKeyNotOwnedException"/> otherwise, before any S3 call is made.
    /// </summary>
    private string AuthorizeHandle(string? handle) => _keys.Authorize(ToLogicalKey(handle));

    private bool TryAuthorizeHandle(string? handle, out string key)
    {
        if (_keys.TryAuthorize(ToLogicalKey(handle), out key))
        {
            return true;
        }

        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation(
                "Skipping S3 object {Handle}: not a storage key owned by tenant {TenantId}.", handle, _keys.TenantId);
        }

        return false;
    }

    /// <summary>
    /// The URL → key mapping, which belongs here and not in a caller: <c>TenantTheme</c> and
    /// <c>AppUser.ImageUrl</c> persist what <see cref="BuildPublicUrl"/> returned, not a key, and
    /// deletion has to find its way back. Strips the scheme and host, the path
    /// <c>PublicBaseUrl</c> may carry, the bucket segment that path-style addressing (MinIO) puts
    /// in front, and the deployment's optional <c>Storage:S3:Prefix</c> — everything the block
    /// itself added. What is left is the logical key, which is then checked, never repaired: the
    /// path is deliberately <b>not</b> percent-unescaped, so an encoded separator stays encoded and
    /// the key grammar refuses it. A backslash is refused the same way — unlike
    /// <see cref="Local.LocalStorageService"/>, which legitimately sees Windows-form disk paths, S3
    /// object keys never contain one, so repairing it here would be exactly the silent normalisation
    /// <see cref="TenantStorageKeyRules.TryAuthorize"/> documents that it never does (#78 hardening
    /// item 1).
    /// </summary>
    private string ToLogicalKey(string? handle)
    {
        if (string.IsNullOrWhiteSpace(handle))
        {
            return string.Empty;
        }

        var value = handle;

        if (Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal)
                || string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)))
        {
            value = StripLeadingSlash(uri.AbsolutePath);
            value = StripSegment(value, PublicBaseUrlPath());
            value = StripSegment(value, _options.Bucket);
        }
        else
        {
            value = StripLeadingSlash(value);
        }

        return StripSegment(value, _options.Prefix);
    }

    /// <summary>The logical key with the deployment's bucket prefix, if any, put back on.</summary>
    private string ToPhysicalKey(string logicalKey) =>
        string.IsNullOrWhiteSpace(_options.Prefix)
            ? logicalKey
            : $"{_options.Prefix.Trim('/')}/{logicalKey}";

    private string? PublicBaseUrlPath() =>
        !string.IsNullOrWhiteSpace(_options.PublicBaseUrl)
        && Uri.TryCreate(_options.PublicBaseUrl, UriKind.Absolute, out var baseUri)
            ? baseUri.AbsolutePath
            : null;

    // Exactly one leading slash: Uri.AbsolutePath always has one, and a persisted local-storage
    // path has one. Trimming them all would turn "//tenants/…" into a key the guard would accept.
    private static string StripLeadingSlash(string value) =>
        value.StartsWith('/') ? value[1..] : value;

    private static string StripSegment(string value, string? segment)
    {
        var trimmed = segment?.Trim('/');
        if (string.IsNullOrEmpty(trimmed))
        {
            return value;
        }

        var prefix = trimmed + "/";
        return value.StartsWith(prefix, StringComparison.Ordinal) ? value[prefix.Length..] : value;
    }

    private static string SanitizeFileName(string fileName)
    {
        return FileNameSanitizer().Replace(fileName, "_");
    }
}