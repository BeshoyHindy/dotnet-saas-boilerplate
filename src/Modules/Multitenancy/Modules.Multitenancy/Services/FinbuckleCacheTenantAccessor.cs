using Boilerplate.BuildingBlocks.Caching;
using Boilerplate.BuildingBlocks.Shared.Multitenancy;
using Finbuckle.MultiTenant.Abstractions;

namespace Boilerplate.Modules.Multitenancy.Services;

/// <summary>
/// Finbuckle-backed <see cref="ICacheTenantAccessor"/>: the Caching building block asks "which tenant
/// is ambient?" and this answers from the one place that knows — Finbuckle's <c>AsyncLocal</c>
/// multi-tenant context, the same source <c>AmbientTenantContext</c> writes through
/// <c>ITenantScope</c>.
///
/// It lives here, not in the block, because a building block may not reference a module. Same seam
/// as <see cref="Boilerplate.BuildingBlocks.Eventing.Abstractions.IEventTenantScope"/> →
/// <see cref="FinbuckleEventTenantScope"/>.
/// </summary>
/// <remarks>
/// Returns <see langword="null"/> rather than a placeholder when nothing is ambient. That null is
/// load-bearing: it is what makes a tenant-less cache read throw instead of silently landing in a
/// shared partition.
/// </remarks>
public sealed class FinbuckleCacheTenantAccessor : ICacheTenantAccessor
{
    private readonly IMultiTenantContextAccessor<AppTenantInfo> _accessor;

    public FinbuckleCacheTenantAccessor(IMultiTenantContextAccessor<AppTenantInfo> accessor)
    {
        ArgumentNullException.ThrowIfNull(accessor);
        _accessor = accessor;
    }

    /// <inheritdoc />
    public string? TenantId => _accessor.MultiTenantContext?.TenantInfo?.Id;
}
