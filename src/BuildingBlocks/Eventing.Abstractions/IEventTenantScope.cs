namespace Boilerplate.BuildingBlocks.Eventing.Abstractions;

/// <summary>
/// Establishes the tenant an integration event is dispatched under, and hands back the service
/// provider its handlers must be resolved from.
///
/// The scope returns the provider rather than only a restore handle so the ordering cannot be got
/// wrong: a <c>MultiTenantDbContext</c> captures its <c>TenantInfo</c> — and with it the tenant's
/// connection string — at construction, so a handler resolved from a DI scope created <i>before</i>
/// the tenant is installed reads the wrong database through a null tenant filter. A caller that
/// creates its own scope has to remember the order; a caller handed one cannot forget it.
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
    /// Begins the dispatch scope for <paramref name="tenantId"/>. A null/whitespace id means a
    /// global event and leaves the ambient tenant alone — the bus only allows that for events that
    /// declare themselves <see cref="IGlobalIntegrationEvent"/>.
    /// Disposing the handle tears down the DI scope and restores the previous ambient tenant.
    /// </summary>
    Task<IEventTenantScopeHandle> BeginAsync(string? tenantId, CancellationToken cancellationToken = default);
}

/// <summary>An open event-dispatch scope.</summary>
public interface IEventTenantScopeHandle : IDisposable
{
    /// <summary>Resolve handlers — and anything they need — from here, never from the root provider.</summary>
    IServiceProvider Services { get; }
}
