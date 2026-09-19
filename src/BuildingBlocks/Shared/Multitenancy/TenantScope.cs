using Finbuckle.MultiTenant.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Boilerplate.BuildingBlocks.Shared.Multitenancy;

/// <summary>
/// Finbuckle-backed <see cref="ITenantScope"/>. Registered as a singleton: it holds no per-tenant
/// state and reaches the (scoped) tenant store through <see cref="IServiceScopeFactory"/>.
/// </summary>
public sealed class TenantScope : ITenantScope
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly AmbientTenantContext _ambient;

    public TenantScope(IServiceScopeFactory scopeFactory, AmbientTenantContext ambient)
    {
        _scopeFactory = scopeFactory;
        _ambient = ambient;
    }

    public async Task RunAsync(
        string tenantId,
        Func<IServiceProvider, CancellationToken, Task> work,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);

        await RunAsync<object?>(
            tenantId,
            async (services, ct) =>
            {
                await work(services, ct).ConfigureAwait(false);
                return null;
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<TResult> RunAsync<TResult>(
        string tenantId,
        Func<IServiceProvider, CancellationToken, Task<TResult>> work,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);

        var tenant = await GetTenantAsync(tenantId, cancellationToken).ConfigureAwait(false);
        return await RunForAsync(tenant, work, cancellationToken).ConfigureAwait(false);
    }

    public async Task RunForEachTenantAsync(
        Func<AppTenantInfo, IServiceProvider, CancellationToken, Task> work,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);

        foreach (var tenant in await GetTenantsAsync(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();

            await RunForAsync<object?>(
                tenant,
                async (services, ct) =>
                {
                    await work(tenant, services, ct).ConfigureAwait(false);
                    return null;
                },
                cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<AppTenantInfo> GetTenantAsync(string tenantId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        cancellationToken.ThrowIfCancellationRequested();

        // Short-lived lookup scope, disposed before any work scope exists — the EF-backed store is
        // scoped, and holding it open would keep a second DbContext (and its connection) alive
        // alongside the tenant's own.
        using var lookupScope = _scopeFactory.CreateScope();

        // Registration order, not type: MultitenancyModule registers the 60-minute distributed cache
        // store before the EF store, the same order the HTTP path already trusts. Ask each store in
        // turn and stop at the first hit, so a job or a dispatched event costs a catalog SELECT only
        // on a cache miss.
        var stores = lookupScope.ServiceProvider
            .GetRequiredService<IEnumerable<IMultiTenantStore<AppTenantInfo>>>()
            .ToList();

        for (var i = 0; i < stores.Count; i++)
        {
            var tenant = await stores[i].GetAsync(tenantId).ConfigureAwait(false);
            if (tenant is null)
            {
                continue;
            }

            if (i > 0)
            {
                // Warm the first (cache) store, mirroring what OnTenantResolveCompleted does for the
                // HTTP path — the next lookup, from any caller, is then a cache hit too.
                await stores[0].AddAsync(tenant).ConfigureAwait(false);
            }

            return tenant;
        }

        throw UnknownTenantException.ForTenant(tenantId);
    }

    public async Task<IReadOnlyList<AppTenantInfo>> GetTenantsAsync(CancellationToken cancellationToken = default)
    {
        using var lookupScope = _scopeFactory.CreateScope();

        // The cache store can't enumerate — Finbuckle's DistributedCacheStore.GetAllAsync throws
        // NotImplementedException, it only ever holds what it was asked to look up by id. Read the
        // authoritative store instead: stores are registered cache-first (see GetTenantAsync), so the
        // authoritative one is the LAST registered.
        var store = lookupScope.ServiceProvider
            .GetRequiredService<IEnumerable<IMultiTenantStore<AppTenantInfo>>>()
            .Last();
        var tenants = await store.GetAllAsync().ConfigureAwait(false);
        return tenants.ToList();
    }

    public ITenantScopeHandle Begin(AppTenantInfo tenant)
    {
        ArgumentNullException.ThrowIfNull(tenant);

        // Order is the whole point: ambient context first, DI scope second. Synchronous so the
        // AsyncLocal write happens in the caller's own flow — see ITenantScope.Begin.
        var restore = _ambient.Enter(tenant);
        try
        {
            return new Handle(_scopeFactory.CreateScope(), restore, tenant);
        }
        catch
        {
            restore.Dispose();
            throw;
        }
    }

    private async Task<TResult> RunForAsync<TResult>(
        AppTenantInfo tenant,
        Func<IServiceProvider, CancellationToken, Task<TResult>> work,
        CancellationToken cancellationToken)
    {
        // Begin and work share this method's execution context, so the ambient tenant the handle
        // installs is visible to everything the work resolves.
        using var handle = Begin(tenant);
        return await work(handle.Services, cancellationToken).ConfigureAwait(false);
    }

    private sealed class Handle : ITenantScopeHandle
    {
        private readonly IServiceScope _scope;
        private readonly IDisposable _restore;
        private bool _disposed;

        public Handle(IServiceScope scope, IDisposable restore, AppTenantInfo tenant)
        {
            _scope = scope;
            _restore = restore;
            Tenant = tenant;
        }

        public IServiceProvider Services => _scope.ServiceProvider;

        public AppTenantInfo Tenant { get; }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            // Scope first, ambient context second: scoped services disposed here can still touch a
            // tenant-filtered DbContext, and must see the tenant they were built under.
            try
            {
                _scope.Dispose();
            }
            finally
            {
                _restore.Dispose();
            }
        }
    }
}
