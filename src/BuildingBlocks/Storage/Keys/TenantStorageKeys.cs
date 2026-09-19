using Boilerplate.BuildingBlocks.Shared.Multitenancy;
using Finbuckle.MultiTenant.Abstractions;

namespace Boilerplate.BuildingBlocks.Storage.Keys;

/// <summary>
/// Reads the ambient Finbuckle tenant on every call — never caches it — so one instance is correct
/// inside an HTTP request, inside a job's <c>ITenantScope</c>, and across the tenant switches a
/// per-tenant fan-out makes. Registered as a singleton, like the accessor it wraps.
/// </summary>
public sealed class TenantStorageKeys(IMultiTenantContextAccessor<AppTenantInfo> tenantAccessor)
    : ITenantStorageKeys
{
    public string TenantId
    {
        get
        {
            var id = tenantAccessor.MultiTenantContext?.TenantInfo?.Id;

            if (string.IsNullOrWhiteSpace(id))
            {
                throw new MissingStorageTenantException();
            }

            return TenantStorageKeyRules.IsValidTenantId(id)
                ? id
                : throw new MissingStorageTenantException(
                    "The ambient tenant id is not a slug, so it cannot own a storage key " +
                    $"({TenantStorageKeyRules.TenantIdPattern}).");
        }
    }

    public string Compose(StorageSpace space, string relativePath) =>
        TenantStorageKeyRules.Compose(TenantId, space, relativePath);

    public string Authorize(string? storageKey) =>
        TryAuthorize(storageKey, out var key) ? key : throw new StorageKeyNotOwnedException();

    public bool TryAuthorize(string? storageKey, out string key) =>
        TenantStorageKeyRules.TryAuthorize(TenantId, storageKey, out key);
}
