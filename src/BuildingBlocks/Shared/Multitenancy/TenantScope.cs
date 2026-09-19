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

    public async Task<ITenantScopeHandle> BeginAsync(string tenantId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        var tenant = await LoadAsync(tenantId, cancellationToken).ConfigureAwait(false);
        return Begin(tenant);
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

        var tenant = await LoadAsync(tenantId, cancellationToken).ConfigureAwait(false);
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

    public async Task<IReadOnlyList<AppTenantInfo>> GetTenantsAsync(CancellationToken cancellationToken = default)
    {
        // Short-lived lookup scope, disposed before any work scope exists — the EF-backed store is
        // scoped, and holding it open would keep a second DbContext (and its connection) alive
        // alongside the tenant's own.
        using var lookupScope = _scopeFactory.CreateScope();
        var store = lookupScope.ServiceProvider.GetRequiredService<IMultiTenantStore<AppTenantInfo>>();
        var tenants = await store.GetAllAsync().ConfigureAwait(false);
        return tenants.ToList();
    }

    private Handle Begin(AppTenantInfo tenant)
    {
        // Order is the whole point: ambient context first, DI scope second.
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
        using var handle = Begin(tenant);
        return await work(handle.Services, cancellationToken).ConfigureAwait(false);
    }

    private async Task<AppTenantInfo> LoadAsync(string tenantId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var lookupScope = _scopeFactory.CreateScope();
        var store = lookupScope.ServiceProvider.GetRequiredService<IMultiTenantStore<AppTenantInfo>>();

        return await store.GetAsync(tenantId).ConfigureAwait(false)
            ?? throw UnknownTenantException.ForTenant(tenantId);
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
