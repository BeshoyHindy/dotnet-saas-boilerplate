# Storage & file uploads

`src/BuildingBlocks/Storage/`. Read before working with files/blobs.

## `IStorageService`

`UploadAsync<T>(FileUploadRequest, FileType, ct)`, `RemoveAsync(path, ct)`, `DownloadAsync`, `ExistsAsync`, `GetSizeAsync` (0 if absent), `GenerateUploadUrlAsync`/`GenerateDownloadUrlAsync` (presigned), `HeadObjectAsync`, `BuildPublicUrl(key)→string` (string, not Uri — local storage returns a server-relative path).

`FileType`: `Image` (5MB), `Document`, `Pdf` (10MB) — `FileTypeMetadata.GetRules` enforces extension + size. **Always propagate `CancellationToken`.**

## Providers

`AddHeroStorage(config)` reads `Storage:Provider` **eagerly at registration**: `"s3"` → `S3StorageService` (supports MinIO via `ServiceUrl` + `ForcePathStyle`), else `LocalStorageService`. The chosen implementation is registered directly as `IStorageService`.

## Presigned upload flow (preferred for user uploads)

Don't stream large files through the API. The pattern (see Files module):
1. `RequestUploadUrl` — server validates category/extension/size, returns a presigned PUT URL, persists a `PendingUpload` record.
2. Client uploads **directly** to storage.
3. `FinalizeUpload` — verifies the stored object, flips to `Available`, publishes `FileFinalizedIntegrationEvent`.

Local/dev without MinIO uses `LocalPresignTokenStore` (in-memory one-shot tokens) — issued but never consumed by any endpoint, so the Files upload flow effectively needs the `s3` provider.

## `BuildPublicUrl` vs `GenerateDownloadUrlAsync`

`BuildPublicUrl` is only valid for keys the bucket policy actually publishes — i.e. `uploads/` (avatars, tenant theme), which is what `UploadAsync` writes. **Never** use it for the Files module's `tenants/…` keys: that prefix has no anonymous grant (contract-tested in `deploy/dokploy/tests/`), so the link 403s, and granting it would publish every private file. Public Files assets go through `GenerateDownloadUrlAsync` with a short TTL instead (`PublicFileUrlFactory`, see `modules/files.md`).

## Test gotcha

`AddHeroStorage` reads `Storage:Provider` **before** a test factory's in-memory config overlay applies, so it wires `LocalStorageService`. Integration tests that need MinIO must **remove the `IStorageService`/`LocalStorageService`/`S3StorageService` descriptors post-registration and re-register the S3 stack** pointed at the MinIO container (see `AppWebApplicationFactory`). See `integration-testing.md`.
