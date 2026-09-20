using Finbuckle.MultiTenant;
using Finbuckle.MultiTenant.Abstractions;

namespace Boilerplate.BuildingBlocks.Shared.Multitenancy;

/// <summary>
/// The single place in the product that writes Finbuckle's ambient tenant context
/// (<see cref="IMultiTenantContextSetter"/>, an <c>AsyncLocal</c>).
///
/// Everything that needs to "become" a tenant outside a request — jobs, event dispatch,
/// provisioning, migrations, seeding — goes through <see cref="ITenantScope"/>, which goes through
/// here. An architecture test pins <see cref="IMultiTenantContextSetter"/> to this file so the
/// hand-rolled "create a scope, then set the tenant" blocks cannot come back: that order is wrong,
/// because a <c>MultiTenantDbContext</c> captures its <c>TenantInfo</c> — and with it the tenant
/// query filter — at construction.
/// </summary>
public sealed class AmbientTenantContext
{
    private readonly IMultiTenantContextAccessor<AppTenantInfo> _accessor;
    private readonly IMultiTenantContextSetter _setter;

    public AmbientTenantContext(
        IMultiTenantContextAccessor<AppTenantInfo> accessor,
        IMultiTenantContextSetter setter)
    {
        _accessor = accessor;
        _setter = setter;
    }

    /// <summary>The tenant currently installed on this async flow, or null when there is none.</summary>
    public AppTenantInfo? Current => _accessor.MultiTenantContext.TenantInfo;

    /// <summary>
    /// Installs <paramref name="tenant"/> as the ambient tenant. Disposing the returned handle
    /// restores whatever was ambient before — including on the exception path, because callers
    /// dispose it from a <c>using</c>/<c>finally</c>.
    /// </summary>
    public IDisposable Enter(AppTenantInfo tenant)
    {
        ArgumentNullException.ThrowIfNull(tenant);

        var previous = _accessor.MultiTenantContext;
        _setter.MultiTenantContext = new MultiTenantContext<AppTenantInfo>(tenant);
        return new Restore(_setter, previous);
    }

    private sealed class Restore : IDisposable
    {
        private readonly IMultiTenantContextSetter _setter;
        private readonly IMultiTenantContext<AppTenantInfo> _previous;
        private bool _disposed;

        public Restore(IMultiTenantContextSetter setter, IMultiTenantContext<AppTenantInfo> previous)
        {
            _setter = setter;
            _previous = previous;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _setter.MultiTenantContext = _previous;
        }
    }
}
