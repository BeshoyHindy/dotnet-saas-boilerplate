# Storage & file uploads

`src/BuildingBlocks/Storage/`. Read before working with files/blobs.

## `IStorageService`

`ComposeKey(space, relativePath)`, `UploadAsync<T>(FileUploadRequest, FileType, ct)`, `RemoveAsync(path, ct)`, `RemoveIfOwnedAsync(handle, ct)`, `DownloadAsync`, `ExistsAsync`, `GetSizeAsync` (0 if absent), `GenerateUploadUrlAsync`/`GenerateDownloadUrlAsync` (presigned), `HeadObjectAsync`, `BuildPublicUrl(key)→string` (string, not Uri — local storage returns a server-relative path).

`FileType`: `Image` (5MB), `Document`, `Pdf` (10MB) — `FileTypeMetadata.GetRules` enforces extension + size. **Always propagate `CancellationToken`.**

## Providers

`AddHeroStorage(config)` reads `Storage:Provider` **eagerly at registration**: `"s3"` → `S3StorageService` (supports MinIO via `ServiceUrl` + `ForcePathStyle`), else `LocalStorageService`. The chosen implementation is registered directly as `IStorageService`.

**Production refuses to boot on Local.** `ProductionConfigurationGuard` fails fast unless `Storage:Provider=s3` (with a `Storage:S3:Bucket`) or the deployment opts in with `Storage:AllowLocalProviderInProduction=true` — Local serves every object anonymously out of `wwwroot`, which would publish private Files objects. `appsettings.Production.json` ships `s3`; compose and Dokploy set it too.

## Presigned upload flow (preferred for user uploads)

Don't stream large files through the API. The pattern (see Files module):
1. `RequestUploadUrl` — server validates category/extension/size, returns a presigned PUT URL, persists a `PendingUpload` record.
2. Client uploads **directly** to storage.
3. `FinalizeUpload` — verifies the stored object, flips to `Available`, publishes `FileFinalizedIntegrationEvent`.

Local/dev without MinIO uses `LocalPresignTokenStore` (in-memory one-shot tokens) — issued but never consumed by any endpoint, so the Files upload flow effectively needs the `s3` provider. A token carries an already-authorized physical key and `Consume(token, tenantId)` re-checks that key's owner, so a token is not a bearer capability for whatever it happens to name.

## The block prefixes the tenant — you never do (ADR-0002)

**Two key spaces, both prefixed from the ambient tenant.** `StorageSpace.Private` is `tenants/{tenantId}/…`; `StorageSpace.Public` is `uploads/tenants/{tenantId}/…`. You name an object with a tenant-**relative** path plus the space and get back an opaque handle to persist:

```csharp
var key = storage.ComposeKey(StorageSpace.Private, $"{ownerType}/{yyyy}/{MM}/{id:N}/{file}");
```

`UploadAsync<T>` is the public-space shortcut (avatars, brand assets): it composes `uploads/tenants/{tenantId}/{typeName}/{guid}_{file}` and returns `BuildPublicUrl` of it.

**Never write `tenants/` or `uploads/` into a key, and never interpolate a tenant id into one.** `Architecture.Tests/StorageKeyOwnershipTests` scans production sources for both. `StorageKeyBuilder` (Files) builds the relative part only.

**Every method that takes a stored handle refuses one this tenant doesn't own**, before any call reaches the backend: `StorageKeyNotOwnedException`, which is a `NotFoundException` → **404, not 403** (a 403 answers the existence question). Refused: another tenant's key, a tenant id yours is merely a prefix of (`acme` vs `acme-2`), `..`, `//`, a leading slash, a backslash, a percent-encoded separator, any case variant of a root, and a pre-#78 flat `uploads/{typeName}/…` key. A handle may be the key **or** the URL a previous `BuildPublicUrl`/`GenerateDownloadUrlAsync` returned — mapping a URL back to a key is the block's job, not yours.

**No ambient tenant → `MissingStorageTenantException`.** There is no fallback and no tenant-less space. Work outside a request (jobs, fan-outs, anything at startup) must enter the tenant through `ITenantScope` first, as the Files purge jobs do — and so must a test that pokes storage directly.

**Deleting what you replaced:** use `RemoveIfOwnedAsync`, not `RemoveAsync`. Columns like `AppUser.ImageUrl` and `TenantTheme.LogoUrl` hold a URL a user may have pasted, or a key from before this rule; those are skipped and logged rather than turned into a 500. `RemoveAsync` is strict and throws.

Still true, and still worth doing: never pass a caller-supplied string to `DownloadAsync`/`GenerateDownloadUrlAsync` — look the key up from a tenant-filtered row first, as the Files module does. The block is the floor, not the plan.

## `BuildPublicUrl` vs `GenerateDownloadUrlAsync`

`BuildPublicUrl` is only valid for keys the bucket policy actually publishes — i.e. the public space under `uploads/` (avatars, tenant theme), which is what `UploadAsync` writes. **Never** use it for a private-space key: that prefix has no anonymous grant (contract-tested in `deploy/dokploy/tests/`), so the link 403s, and granting it would publish every private file. Public Files assets go through `GenerateDownloadUrlAsync` with a short TTL instead (`PublicFileUrlFactory`, see `modules/files.md`). Nesting the public space *under* `uploads/` is deliberate: the one anonymous-read grant in `deploy/` keeps covering exactly it, and nothing under `tenants/`.

## Test gotcha

`AddHeroStorage` reads `Storage:Provider` **before** a test factory's in-memory config overlay applies, so it wires `LocalStorageService`. Integration tests that need MinIO must **remove the `IStorageService`/`LocalStorageService`/`S3StorageService` descriptors post-registration and re-register the S3 stack** pointed at the MinIO container (see `AppWebApplicationFactory`). See `integration-testing.md`.

The test bucket carries the deploy stacks' grant — anonymous GET on `uploads/` and nowhere else — so an assertion that a durable avatar URL *works* is the real answer, and `tenants/` staying closed is testable.

A test that resolves `IStorageService` from a bare `CreateScope()` will throw `MissingStorageTenantException`: go through `ITenantScope.RunAsync(tenantId, …)`.
