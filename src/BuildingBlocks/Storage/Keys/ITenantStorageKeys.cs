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
