namespace Boilerplate.BuildingBlocks.Eventing.Abstractions;

/// <summary>
/// Runs an integration event's dispatch under the tenant that event belongs to, and hands the
/// dispatch the service provider its handlers must be resolved from.
///
/// Callback rather than a returned handle, for two reasons:
/// <list type="bullet">
///   <item>the DI scope comes from here, so "install the tenant, <i>then</i> create the scope"
///     cannot be got wrong by a caller — a <c>MultiTenantDbContext</c> captures its
///     <c>TenantInfo</c>, and with it the tenant's connection string, at construction;</item>
///   <item>the ambient tenant is an <c>AsyncLocal</c>, and a write made in the continuation of an
///     <c>async</c> method is discarded when that method returns. A <c>Task&lt;handle&gt;</c> API
///     would therefore hand back a handle whose tenant is no longer ambient for the caller. Running
///     the dispatch inside this method keeps the write and its use in one execution context.</item>
/// </list>
///
/// Implementations load the <b>full</b> tenant record from the tenant store. An id-only stub is what
/// made per-tenant connection strings silently fall back to the default database.
///
/// The default implementation (<c>NullEventTenantScope</c>) just opens a DI scope; the multitenancy
/// composition replaces it with a Finbuckle-backed one. Keeping the abstraction here lets the event
/// bus stay tenant-technology-agnostic.
/// </summary>
public interface IEventTenantScope
{
    /// <summary>
    /// Invokes <paramref name="dispatch"/> under <paramref name="tenantId"/>. A null/whitespace id
    /// means a global event and leaves the ambient tenant alone — the bus only allows that for
    /// events that declare themselves <see cref="IGlobalIntegrationEvent"/>.
    /// </summary>
    Task DispatchAsync(
        string? tenantId,
        Func<IServiceProvider, CancellationToken, Task> dispatch,
        CancellationToken cancellationToken = default);
}
