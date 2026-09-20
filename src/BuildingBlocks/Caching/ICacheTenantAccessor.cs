namespace Boilerplate.BuildingBlocks.Caching;

/// <summary>
/// The Caching block's one window onto tenancy: "which tenant is ambient on this async flow?".
///
/// Deliberately declared here rather than taken from the Multitenancy module — a building block may
/// not reference a module (<c>BuildingBlocksIndependenceTests</c>), and the block needs one fact, not
/// Finbuckle. The Multitenancy module supplies the Finbuckle-backed implementation, the same seam as
/// <c>IEventTenantScope</c> → <c>FinbuckleEventTenantScope</c>.
/// </summary>
public interface ICacheTenantAccessor
{
    /// <summary>
    /// The ambient tenant id, or <see langword="null"/> when no tenant is established on this async
    /// flow — host startup, a JWT authentication event (which runs before tenant resolution), a
    /// hosted service outside <c>ITenantScope</c>.
    /// <para>
    /// Never invent a value here. A fallback tenant is precisely the bug the tenant prefix exists to
    /// prevent: it silently merges every tenant-less caller into one shared partition.
    /// </para>
    /// </summary>
    string? TenantId { get; }
}
