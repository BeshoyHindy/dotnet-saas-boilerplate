using Boilerplate.BuildingBlocks.Shared.Multitenancy;
using Boilerplate.BuildingBlocks.Storage.Keys;
using Finbuckle.MultiTenant;
using Finbuckle.MultiTenant.Abstractions;

namespace Framework.Tests.Storage;

/// <summary>
/// The real <see cref="TenantStorageKeys"/> over a tenant the test can switch at will, so a
/// provider test can do the one thing that matters here: perform the same operation as two
/// different tenants and watch the second be refused.
/// </summary>
internal sealed class AmbientTenantStorageKeys : ITenantStorageKeys
{
    private readonly TenantStorageKeys _inner;

    public AmbientTenantStorageKeys(string? tenantId = "acme")
    {
        // An empty context, as Finbuckle hands out before resolution: present, but with no tenant.
        var empty = Substitute.For<IMultiTenantContext<AppTenantInfo>>();
        empty.TenantInfo.Returns((AppTenantInfo?)null);

        var accessor = Substitute.For<IMultiTenantContextAccessor<AppTenantInfo>>();
        accessor.MultiTenantContext.Returns(_ => Current is null
            ? empty
            : new MultiTenantContext<AppTenantInfo>(new AppTenantInfo { Id = Current, Identifier = Current, Name = Current }));

        Current = tenantId;
        _inner = new TenantStorageKeys(accessor);
    }

    /// <summary>The ambient tenant id, or null for "no tenant at all".</summary>
    public string? Current { get; set; }

    public string TenantId => _inner.TenantId;

    public string Compose(StorageSpace space, string relativePath) => _inner.Compose(space, relativePath);

    public string Authorize(string? storageKey) => _inner.Authorize(storageKey);

    public bool TryAuthorize(string? storageKey, out string key) => _inner.TryAuthorize(storageKey, out key);
}
