using Boilerplate.BuildingBlocks.Shared.Storage;
using Boilerplate.BuildingBlocks.Storage.DTOs;
using Boilerplate.BuildingBlocks.Storage.Keys;
using Boilerplate.BuildingBlocks.Storage.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Logging;
using System.Text.RegularExpressions;
using System.Threading;

namespace Boilerplate.BuildingBlocks.Storage.Local;

/// <summary>
/// The development fallback: objects are files under <c>wwwroot</c>, served by
/// <c>UseStaticFiles</c>. Key composition and the ownership check are the same as the S3 provider's
/// — the tenant prefix becomes a directory level, and a key outside the ambient tenant's two
/// prefixes is refused before any path is touched. Production refuses to boot on this provider
/// (<c>ProductionConfigurationGuard</c>) because it serves everything it stores anonymously.
/// </summary>
public sealed partial class LocalStorageService : IStorageService
{
    // Source-generated, compiled once — the inline Regex.Replace calls re-parsed the pattern on every upload.
    [GeneratedRegex("[^a-z0-9]")]
    private static partial Regex FolderSanitizer();

    [GeneratedRegex(@"[^a-zA-Z0-9_\.-]")]
    private static partial Regex FileNameSanitizer();
    private readonly string _rootPath;
    private readonly ITenantStorageKeys _keys;
    private readonly ILogger<LocalStorageService> _logger;
    private readonly FileExtensionContentTypeProvider _contentTypeProvider;

    public LocalStorageService(
        IWebHostEnvironment environment,
        ITenantStorageKeys keys,
        ILogger<LocalStorageService> logger)
    {
        ArgumentNullException.ThrowIfNull(environment);
        _rootPath = string.IsNullOrWhiteSpace(environment.WebRootPath)
            ? Path.Combine(environment.ContentRootPath, "wwwroot")
            : environment.WebRootPath;
        _keys = keys;
        _logger = logger;
        _contentTypeProvider = new FileExtensionContentTypeProvider();
    }

    public async Task<string> UploadAsync<T>(FileUploadRequest request, FileType fileType, CancellationToken cancellationToken = default)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(request);

        var rules = FileTypeMetadata.GetRules(fileType);
        var extension = Path.GetExtension(request.FileName);

        if (string.IsNullOrWhiteSpace(extension) ||
            !rules.AllowedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"File type '{extension}' is not allowed. Allowed: {string.Join(", ", rules.AllowedExtensions)}");
        }

        if (request.Data.Count > rules.MaxSizeInMB * 1024 * 1024)
        {
            throw new InvalidOperationException($"File exceeds max size of {rules.MaxSizeInMB} MB.");
        }

#pragma warning disable CA1308 // folder names are intentionally lower-case for URLs/paths
        var folder = FolderSanitizer().Replace(typeof(T).Name.ToLowerInvariant(), "_");
#pragma warning restore CA1308
        var safeFileName = $"{Guid.NewGuid():N}_{SanitizeFileName(request.FileName)}";
        var key = _keys.Compose(StorageSpace.Public, $"{folder}/{safeFileName}");
        var fullPath = ResolveDiskPath(key);

        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        await File.WriteAllBytesAsync(fullPath, request.Data.ToArray(), cancellationToken);

        // The key itself, '/'-separated: it is both the handle and — because wwwroot is served
        // verbatim — the server-relative URL, modulo the leading slash BuildPublicUrl adds.
        return key;
    }

    public string ComposeKey(StorageSpace space, string relativePath) => _keys.Compose(space, relativePath);

    public Task<FileDownloadResponse?> DownloadAsync(string path, CancellationToken cancellationToken = default)
    {
        var fullPath = ResolveDiskPath(AuthorizeHandle(path));

        if (!File.Exists(fullPath))
        {
            return Task.FromResult<FileDownloadResponse?>(null);
        }

        var fileInfo = new FileInfo(fullPath);
        var fileName = Path.GetFileName(fullPath);

        if (!_contentTypeProvider.TryGetContentType(fileName, out var contentType))
        {
            contentType = "application/octet-stream";
        }

        var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);

        return Task.FromResult<FileDownloadResponse?>(new FileDownloadResponse
        {
            Stream = stream,
            ContentType = contentType,
            FileName = fileName,
            ContentLength = fileInfo.Length
        });
    }

    public Task<bool> ExistsAsync(string path, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(File.Exists(ResolveDiskPath(AuthorizeHandle(path))));
    }

    public Task<long> GetSizeAsync(string path, CancellationToken cancellationToken = default)
    {
        var fullPath = ResolveDiskPath(AuthorizeHandle(path));

        return Task.FromResult(File.Exists(fullPath) ? new FileInfo(fullPath).Length : 0L);
    }

    public Task RemoveAsync(string path, CancellationToken cancellationToken = default)
    {
        var key = AuthorizeHandle(path);
        return RemoveAuthorizedAsync(key);
    }

    public async Task<bool> RemoveIfOwnedAsync(string? storedHandle, CancellationToken cancellationToken = default)
    {
        if (!TryAuthorizeHandle(storedHandle, out var key))
        {
            return false;
        }

        await RemoveAuthorizedAsync(key).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Deletes an already-authorized key. Both public delete entry points route here instead of
    /// calling each other, so ownership is checked exactly once per call rather than
    /// <see cref="RemoveIfOwnedAsync"/> re-entering the public <see cref="RemoveAsync"/> and running
    /// <see cref="AuthorizeHandle"/> a second time (#78 hardening item 3).
    /// </summary>
    private Task RemoveAuthorizedAsync(string key)
    {
        var fullPath = ResolveDiskPath(key);

        if (File.Exists(fullPath))
        {
            File.Delete(fullPath);
        }

        return Task.CompletedTask;
    }

    private static string SanitizeFileName(string fileName)
    {
        return FileNameSanitizer().Replace(fileName, "_");
    }

    // Dev-only presigning fallback when Storage:Provider != s3 (prod uses S3StorageService). Token
    // store is process-static so the dev middleware can consume the token without re-resolving DI.
    private static LocalPresignTokenStore? _staticTokenStore;
    public static LocalPresignTokenStore SharedTokenStore => LazyInitializer.EnsureInitialized(ref _staticTokenStore);

    public Task<PresignedUploadUrl> GenerateUploadUrlAsync(
        string storageKey, string contentType, long maxBytes, TimeSpan ttl,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);

        // The token carries the authorized physical key, which is what binds it to one tenant:
        // LocalPresignTokenStore.Consume re-checks ownership, so a token minted under tenant A
        // cannot be redeemed for tenant B's object even if the token string leaks.
        var token = SharedTokenStore.Issue(AuthorizeHandle(storageKey), contentType, maxBytes, ttl);
        var url = new Uri($"local://upload/{token}", UriKind.Absolute);
        var headers = new Dictionary<string, string>(StringComparer.Ordinal) { ["Content-Type"] = contentType };
        return Task.FromResult(new PresignedUploadUrl(url, headers, DateTimeOffset.UtcNow.Add(ttl)));
    }

    public Task<Uri> GenerateDownloadUrlAsync(
        string storageKey, TimeSpan ttl, string? responseContentDisposition = null,
        CancellationToken cancellationToken = default)
    {
        // Local mode serves files from /wwwroot — no signing required.
        return Task.FromResult(new Uri($"/{AuthorizeHandle(storageKey)}", UriKind.Relative));
    }

#pragma warning disable CA1055 // returns a server-relative path, not a well-formed Uri — see IStorageService.BuildPublicUrl
    public string BuildPublicUrl(string storageKey)
#pragma warning restore CA1055
    {
        // Resolved later against the dashboard's API origin (as UserProfileService does for
        // relative avatars). The leading slash lets clients distinguish absolute from
        // server-relative URLs.
        return $"/{AuthorizeHandle(storageKey)}";
    }

    public Task<StoredObjectMetadata?> HeadObjectAsync(
        string storageKey, CancellationToken cancellationToken = default)
    {
        var fullPath = ResolveDiskPath(AuthorizeHandle(storageKey));
        if (!File.Exists(fullPath))
        {
            return Task.FromResult<StoredObjectMetadata?>(null);
        }

        var info = new FileInfo(fullPath);
        if (!_contentTypeProvider.TryGetContentType(info.Name, out var contentType))
        {
            contentType = "application/octet-stream";
        }

        return Task.FromResult<StoredObjectMetadata?>(new StoredObjectMetadata(
            info.Length,
            contentType!,
            new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
            ETag: null));
    }

    /// <summary>
    /// Maps a persisted handle back to a key and proves the ambient tenant owns it. The handle may
    /// be the key, or the server-relative URL <see cref="BuildPublicUrl"/> and
    /// <see cref="GenerateDownloadUrlAsync"/> hand out — one leading slash, no host — so exactly
    /// one leading slash is removed and separators are normalised. Nothing else is repaired.
    /// </summary>
    private string AuthorizeHandle(string? handle) => _keys.Authorize(ToKey(handle));

    private bool TryAuthorizeHandle(string? handle, out string key)
    {
        if (_keys.TryAuthorize(ToKey(handle), out key))
        {
            return true;
        }

        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation(
                "Skipping local object {Handle}: not a storage key owned by tenant {TenantId}.", handle, _keys.TenantId);
        }

        return false;
    }

    private static string ToKey(string? handle)
    {
        if (string.IsNullOrWhiteSpace(handle))
        {
            return string.Empty;
        }

        var value = handle.Replace('\\', '/');
        return value.StartsWith('/') ? value[1..] : value;
    }

    /// <summary>
    /// The on-disk path for an already-authorized key. The key grammar rules out <c>..</c> and
    /// absolute paths, so this cannot escape <c>wwwroot</c>; the containment check is the belt to
    /// that braces, and fails loudly rather than reading someone else's file if it ever could.
    /// </summary>
    private string ResolveDiskPath(string key)
    {
        var relative = key.Replace('/', Path.DirectorySeparatorChar);
        var fullPath = Path.GetFullPath(Path.Combine(_rootPath, relative));
        var root = Path.GetFullPath(_rootPath);

        if (!fullPath.StartsWith(
                root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar,
                StringComparison.Ordinal))
        {
            throw new StorageKeyNotOwnedException();
        }

        return fullPath;
    }
}
