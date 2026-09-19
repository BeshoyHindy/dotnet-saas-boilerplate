# Module: Files

Presigned-URL file lifecycle (upload → finalize → serve → delete) shared by avatars and other owning modules. Module `Order = 350` (loads before consumer modules).

**Entities / DbContext:** `FileAsset` (aggregate, soft-deletable): status `PendingUpload → Available | Quarantined`, `Visibility` (Public/Private), `ScanStatus`. `FilesDbContext`. Publishes `FileFinalizedIntegrationEvent`.
**Areas:** RequestUploadUrl, FinalizeUpload, GetFileDownloadUrl/Metadata, ChangeVisibility, Delete/Restore, ListMy/Shared/Trashed. Purge jobs (orphaned hourly, deleted daily). Full list: `Features/v1/` or `/scalar`. Storage mechanics: `storage.md`.

## Gotchas

- **`publicUrl` is a short-lived presigned GET, never a bucket URL and never persisted.** Files objects live under `tenants/…`, a key space public and private files share and that the deploy stacks never grant anonymous read on (only `uploads/` — avatars, tenant theme — is anonymously readable; widening it would publish every private file). `PublicFileUrlFactory` mints the URL on every read from `Files:PublicUrlTtlMinutes` (default 5, clamped 1–15). Consequence, by design: flipping Public → Private stops issuance **immediately**, but a URL already handed out works **until it expires** — hard revocation means deleting the object. Anything that stores a URL on an entity (e.g. an avatar `imageUrl`) must store a `uploads/`-prefixed asset or the FileAsset id, not `publicUrl`.
- **Local storage provider is dev-only and enforces nothing.** It has no signing: `GenerateDownloadUrlAsync` returns a wwwroot-relative path served by `UseStaticFiles`, identical for public and private keys. Visibility is enforced above it, in `PublicFileUrlFactory` + the access policies. Its presigned *upload* URL (`local://upload/…`) has no consuming endpoint, so the module's upload flow needs the `s3` provider (MinIO in compose).
- **Presigned flow** — never stream uploads through the API. RequestUploadUrl validates category/extension/size and persists a `PendingUpload`; client uploads directly to storage; FinalizeUpload verifies the stored object against the declared size/content-type and flips to Available/Quarantined.
- **`FileAccessPolicyRegistry`** resolves `IFileAccessPolicy` by **OwnerType** — case-insensitive, **closed by default** (unknown OwnerType → forbidden), **last-write-wins** on duplicates (intentional, for test substitution). Each owning module registers its own policy in its `ConfigureServices` (owning modules load after Files). Files ships `DefaultUploaderOnlyPolicy` for built-in OwnerTypes `"MyFiles"`/`"User"`.
- `CanChangeVisibilityAsync` defaults to the delete rule (uploader-only); domain-bound files may override to forbid visibility flips.
- Tenant scoping is implicit via `BaseDbContext` (no explicit `TenantId` on `FileAsset`).

To support uploads for a new owner type: implement `IFileAccessPolicy`, register it in the owning module, and use that OwnerType in RequestUploadUrl.
