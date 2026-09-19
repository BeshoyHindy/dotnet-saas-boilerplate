namespace Boilerplate.BuildingBlocks.Shared.Multitenancy;

/// <summary>
/// The one way to enter a tenant outside an HTTP request (ADR-0002, "Jobs and events").
///
/// Every entry point here does the same three things, in this order:
/// <list type="number">
///   <item>load the <b>full</b> <see cref="AppTenantInfo"/> from the tenant store — the record, not a
///     fabricated id-only stub, so a dedicated per-tenant connection string survives;</item>
///   <item>install it as the ambient Finbuckle context;</item>
///   <item><b>then</b> create the DI scope, so every scoped service built inside it — DbContexts and
///     the connections they capture at construction — is constructed under that tenant.</item>
/// </list>
/// Getting that order wrong is the bug this abstraction exists to make unrepresentable: a scope
/// created first hands its DbContexts a null tenant and the default connection string.
/// </summary>
public interface ITenantScope
{
    /// <summary>
    /// Opens a tenant scope. The caller must dispose the handle; disposal tears down the DI scope
    /// and restores the previously ambient tenant.
    /// Prefer <see cref="RunAsync(string, Func{IServiceProvider, CancellationToken, Task}, CancellationToken)"/>
    /// unless the scope's lifetime is owned by a framework (the Hangfire job activator is the one case).
    /// </summary>
    /// <exception cref="UnknownTenantException">The store has no tenant with that id.</exception>
    Task<ITenantScopeHandle> BeginAsync(string tenantId, CancellationToken cancellationToken = default);

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
    /// Every tenant in the store. For callers that need the catalog itself (readiness probes,
    /// startup waits) rather than work done under each tenant.
    /// </summary>
    Task<IReadOnlyList<AppTenantInfo>> GetTenantsAsync(CancellationToken cancellationToken = default);
}

/// <summary>An open tenant scope: the tenant that is ambient, and the DI scope created under it.</summary>
public interface ITenantScopeHandle : IDisposable
{
    /// <summary>Services resolved from here are constructed under <see cref="Tenant"/>.</summary>
    IServiceProvider Services { get; }

    /// <summary>The full tenant record loaded from the store.</summary>
    AppTenantInfo Tenant { get; }
}
