namespace Boilerplate.BuildingBlocks.Caching;

/// <summary>
/// <see cref="ICacheTenantAccessor"/> for a host that has chosen — explicitly, at registration, via
/// <c>AddHeroCaching(configuration, singleTenant: true)</c> — to run without multitenancy.
///
/// Every key then lands under one fixed partition, so the physical key layout is the same shape
/// whether or not the host is multi-tenant and nothing downstream (the idempotency probe, a Redis
/// dump, an ops runbook) needs a second mental model.
/// </summary>
/// <remarks>
/// <see cref="SingleTenantId"/> cannot collide with a real tenant: tenant ids are validated against
/// <c>^[a-z0-9][a-z0-9-]{1,62}$</c>, which admits neither an underscore nor a leading one.
/// </remarks>
public sealed class SingleTenantCacheTenantAccessor : ICacheTenantAccessor
{
    /// <summary>The fixed partition id used when the host declared itself single-tenant.</summary>
    public const string SingleTenantId = "_single";

    /// <inheritdoc />
    public string TenantId => SingleTenantId;
}
