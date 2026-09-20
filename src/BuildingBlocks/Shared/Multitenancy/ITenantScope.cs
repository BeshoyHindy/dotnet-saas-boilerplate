namespace Boilerplate.BuildingBlocks.Shared.Multitenancy;

/// <summary>
/// The one way to enter a tenant outside an HTTP request (ADR-0002, "Jobs and events").
///
/// Every entry point here does the same three things, in this order:
/// <list type="number">
///   <item>load the <b>full</b> <see cref="AppTenantInfo"/> from the tenant store — the record, not an
///     id-only stub, so an unknown or deactivated tenant fails the work closed;</item>
///   <item>install it as the ambient Finbuckle context;</item>
///   <item><b>then</b> create the DI scope, so every scoped service built inside it — every
///     tenant-filtered <c>DbContext</c>, which captures its <c>TenantInfo</c> at construction — is
///     constructed under that tenant.</item>
/// </list>
/// Getting that order wrong is the bug this abstraction exists to make unrepresentable: a scope
/// created first hands its DbContexts a null tenant, and their queries are then unscoped.
/// </summary>
public interface ITenantScope
{
    /// <summary>Runs <paramref name="work"/> inside a scope opened for <paramref name="tenantId"/>.</summary>
    /// <exception cref="UnknownTenantException">The store has no tenant with that id.</exception>
    Task RunAsync(
        string tenantId,
        Func<IServiceProvider, CancellationToken, Task> work,
        CancellationToken cancellationToken = default);

    /// <summary>Result-returning overload of
    /// <see cref="RunAsync(string, Func{IServiceProvider, CancellationToken, Task}, CancellationToken)"/>.</summary>
    /// <exception cref="UnknownTenantException">The store has no tenant with that id.</exception>
    Task<TResult> RunAsync<TResult>(
        string tenantId,
        Func<IServiceProvider, CancellationToken, Task<TResult>> work,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Per-tenant fan-out: runs <paramref name="work"/> once per tenant in the store, each inside its
    /// own tenant scope. Exceptions propagate — a caller that must survive one bad tenant catches
    /// inside <paramref name="work"/>, which is handed the tenant record for its error message.
    /// </summary>
    Task RunForEachTenantAsync(
        Func<AppTenantInfo, IServiceProvider, CancellationToken, Task> work,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads a tenant record. Touches no ambient state. Checks the registered
    /// <see cref="Finbuckle.MultiTenant.Abstractions.IMultiTenantStore{TTenantInfo}"/>s in registration
    /// order — the 60-minute distributed cache first, the EF-backed catalog second — the same trust the
    /// HTTP path already places in the cache, so this is a cache hit unless the tenant was never looked
    /// up before. A hit from a store other than the first warms that first store.
    /// </summary>
    /// <exception cref="UnknownTenantException">No store has a tenant with that id.</exception>
    Task<AppTenantInfo> GetTenantAsync(string tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Every tenant in the catalog. For callers that need the catalog itself (readiness probes,
    /// startup waits) rather than work done under each tenant. Always reads the authoritative,
    /// last-registered store — the cache store cannot enumerate.
    /// </summary>
    Task<IReadOnlyList<AppTenantInfo>> GetTenantsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Enters <paramref name="tenant"/> and opens a DI scope under it, for the one case where a
    /// framework owns the scope's lifetime and <c>RunAsync</c> cannot be used — Hangfire's
    /// <c>JobActivator.BeginScope</c>. Dispose the handle to tear the scope down and restore the
    /// previous ambient tenant.
    ///
    /// <b>Synchronous on purpose, and it has to stay that way.</b> The ambient tenant is an
    /// <c>AsyncLocal</c>, and a write performed in the continuation of an <c>async</c> method is
    /// discarded when that method returns — so an async <c>BeginAsync</c> would hand back a handle
    /// whose tenant is no longer ambient in the caller's flow, and the caller's first
    /// <c>GetRequiredService&lt;SomeDbContext&gt;()</c> would build it with a null tenant. Resolve
    /// the record with <see cref="GetTenantAsync"/> first, then call this from the frame that will
    /// use the scope. Prefer <c>RunAsync</c>, which has no such hazard.
    /// </summary>
    ITenantScopeHandle Begin(AppTenantInfo tenant);
}

/// <summary>An open tenant scope: the tenant that is ambient, and the DI scope created under it.</summary>
public interface ITenantScopeHandle : IDisposable
{
    /// <summary>Services resolved from here are constructed under <see cref="Tenant"/>.</summary>
    IServiceProvider Services { get; }

    /// <summary>The full tenant record loaded from the store.</summary>
    AppTenantInfo Tenant { get; }
}
