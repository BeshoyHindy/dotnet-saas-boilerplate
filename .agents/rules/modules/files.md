# Module: Files

Presigned-URL file lifecycle (upload → finalize → serve → delete) shared by avatars and other owning modules. Module `Order = 350` (loads before consumer modules).

**Entities / DbContext:** `FileAsset` (aggregate, soft-deletable): status `PendingUpload → Available | Quarantined`, `Visibility` (Public/Private), `ScanStatus`. `FilesDbContext`. Publishes `FileFinalizedIntegrationEvent`.
**Areas:** RequestUploadUrl, FinalizeUpload, GetFileDownloadUrl/Metadata, ChangeVisibility, Delete/Restore, ListMy/Shared/Trashed. Purge jobs (orphaned hourly, deleted daily). Full list: `Features/v1/` or `/scalar`. Storage mechanics: `storage.md`.

## Gotchas

- **Presigned flow** — never stream uploads through the API. RequestUploadUrl validates category/extension/size and persists a `PendingUpload`; client uploads directly to storage; FinalizeUpload verifies the stored object against the declared size/content-type and flips to Available/Quarantined.
- **`FileAccessPolicyRegistry`** resolves `IFileAccessPolicy` by **OwnerType** — case-insensitive, **closed by default** (unknown OwnerType → forbidden), **last-write-wins** on duplicates (intentional, for test substitution). Each owning module registers its own policy in its `ConfigureServices` (owning modules load after Files). Files ships `DefaultUploaderOnlyPolicy` for built-in OwnerTypes `"MyFiles"`/`"User"`.
- `CanChangeVisibilityAsync` defaults to the delete rule (uploader-only); domain-bound files may override to forbid visibility flips.
- Tenant scoping is implicit via `BaseDbContext` (no explicit `TenantId` on `FileAsset`).

To support uploads for a new owner type: implement `IFileAccessPolicy`, register it in the owning module, and use that OwnerType in RequestUploadUrl.
