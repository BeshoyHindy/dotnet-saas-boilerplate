using Boilerplate.BuildingBlocks.Eventing.Abstractions;
using Boilerplate.BuildingBlocks.Shared.Multitenancy;
using Microsoft.Extensions.DependencyInjection;

namespace Boilerplate.Modules.Multitenancy.Services;

/// <summary>
/// Finbuckle-backed <see cref="IEventTenantScope"/>, implemented entirely on top of
/// <see cref="ITenantScope"/>: the event's tenant is loaded from the store as a full record and
/// installed before the handlers' DI scope exists, so a handler's DbContext picks up that tenant's
/// own connection string instead of the default one.
///
/// It used to fabricate <c>new AppTenantInfo(tenantId, tenantId)</c> — identity only. That was
/// enough for the row-level filter in the shared-database model and silently wrong for a tenant
/// with a dedicated database: the handler read and wrote the default one.
/// </summary>
public sealed class FinbuckleEventTenantScope : IEventTenantScope
{
    private readonly ITenantScope _tenantScope;
    private readonly IServiceScopeFactory _scopeFactory;

    public FinbuckleEventTenantScope(ITenantScope tenantScope, IServiceScopeFactory scopeFactory)
    {
        _tenantScope = tenantScope;
        _scopeFactory = scopeFactory;
    }

    public async Task<IEventTenantScopeHandle> BeginAsync(string? tenantId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            // Global event — the bus has already checked the type declares itself one. Leave the
            // ambient context alone and just give the handlers a scope.
            return new PlainScopeHandle(_scopeFactory.CreateScope());
        }

        return new TenantScopeHandle(await _tenantScope.BeginAsync(tenantId, cancellationToken).ConfigureAwait(false));
    }

    private sealed class TenantScopeHandle : IEventTenantScopeHandle
    {
        private readonly ITenantScopeHandle _inner;

        public TenantScopeHandle(ITenantScopeHandle inner) => _inner = inner;

        public IServiceProvider Services => _inner.Services;

        public void Dispose() => _inner.Dispose();
    }

    private sealed class PlainScopeHandle : IEventTenantScopeHandle
    {
        private readonly IServiceScope _scope;

        public PlainScopeHandle(IServiceScope scope) => _scope = scope;

        public IServiceProvider Services => _scope.ServiceProvider;

        public void Dispose() => _scope.Dispose();
    }
}
