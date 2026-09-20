namespace Boilerplate.BuildingBlocks.Storage.Keys;

/// <summary>
/// <see cref="TenantStorageKeyRules"/> bound to the ambient tenant. Both storage providers take one
/// and run every key through it, so "which tenant may name this object" is answered in a single
/// place rather than once per provider, per operation.
/// </summary>
public interface ITenantStorageKeys
{
    /// <summary>
    /// The ambient tenant's id.
    /// </summary>
    /// <exception cref="MissingStorageTenantException">
    /// There is no ambient tenant, or its id is not a slug. Never a fallback.
    /// </exception>
    string TenantId { get; }

    /// <summary>
    /// Composes the physical key for a tenant-relative path in <paramref name="space"/>. The result
    /// is an opaque handle the caller may persist.
    /// </summary>
    /// <exception cref="MissingStorageTenantException">There is no ambient tenant.</exception>
    /// <exception cref="ArgumentException"><paramref name="relativePath"/> is not well-formed.</exception>
    string Compose(StorageSpace space, string relativePath);

    /// <summary>
    /// Composes the key for a public asset owned by <paramref name="owner"/> inside the ambient
    /// tenant — <c>uploads/tenants/{tenantId}/{ownerType}/{owner}/{guid}_{fileName}</c>. Every
    /// avatar and brand asset goes through here, so a later delete can ask whether a persisted
    /// handle is one this owner's own upload produced (#83).
    /// </summary>
    /// <exception cref="MissingStorageTenantException">There is no ambient tenant.</exception>
    /// <exception cref="ArgumentException">A part is not reducible to a well-formed segment.</exception>
    string ComposeAsset(string ownerType, string owner, string fileName);

    /// <summary>
    /// <see cref="TryAuthorize"/> narrowed to one owner's public-asset prefix. False — never an
    /// exception — for a handle that is an arbitrary URL, a pre-#83 key without an owner segment, or
    /// another owner's object.
    /// </summary>
    bool TryAuthorizeOwnedAsset(string ownerType, string owner, string? storageKey, out string key);

    /// <summary>Returns <paramref name="storageKey"/> when the ambient tenant owns it.</summary>
    /// <exception cref="MissingStorageTenantException">There is no ambient tenant.</exception>
    /// <exception cref="StorageKeyNotOwnedException">The key lies outside this tenant's two prefixes.</exception>
    string Authorize(string? storageKey);

    /// <summary>
    /// Non-throwing <see cref="Authorize"/>, for the best-effort paths (replacing an avatar or a
    /// logo) that must tolerate a handle written before keys were tenant-prefixed. Still throws
    /// when there is no ambient tenant — that is never a tolerable state.
    /// </summary>
    bool TryAuthorize(string? storageKey, out string key);
}
