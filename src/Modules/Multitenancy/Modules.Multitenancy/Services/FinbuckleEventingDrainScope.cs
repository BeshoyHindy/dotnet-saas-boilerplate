using Boilerplate.BuildingBlocks.Eventing.Abstractions;
using Boilerplate.BuildingBlocks.Shared.Multitenancy;

namespace Boilerplate.Modules.Multitenancy.Services;

/// <summary>
/// Installs an <see cref="AppTenantInfo"/> carrying the drain target's connection string, so an
/// <c>EventingDbContext</c> built inside the scope routes to that tenant's database.
///
/// Deliberately still distinct from <see cref="FinbuckleEventTenantScope"/>: that one wraps the
/// dispatch of one event and names a tenant, this one wraps a drain pass and names a
/// <i>database</i> — several tenants sharing a connection string collapse to one target, so the
/// target is the connection string, not the tenant. It therefore builds the info from the target
/// instead of loading a record from the store. What the two did share — writing and restoring
/// Finbuckle's ambient context — now lives once, in <see cref="AmbientTenantContext"/>.
/// </summary>
public sealed class FinbuckleEventingDrainScope : IEventingDrainScope
{
    private readonly AmbientTenantContext _ambient;

    public FinbuckleEventingDrainScope(AmbientTenantContext ambient) => _ambient = ambient;

    public IDisposable Begin(EventingDrainTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (target.TenantId is null && target.ConnectionString is null)
        {
            // Default pass: leave the ambient context alone so the context falls through to the
            // configured default connection.
            return NoopScope.Instance;
        }

        // Built by hand rather than via the tenant-shaped constructor: only the id and the
        // connection string matter for routing, and the richer constructor also stamps validity
        // and activation state we have no business inventing here.
        var info = new AppTenantInfo(target.TenantId!, target.TenantId!)
        {
            ConnectionString = target.ConnectionString ?? string.Empty,
        };

        return _ambient.Enter(info);
    }

    private sealed class NoopScope : IDisposable
    {
        public static readonly NoopScope Instance = new();

        public void Dispose()
        {
            // Nothing to restore — the ambient context was never touched.
        }
    }
}
