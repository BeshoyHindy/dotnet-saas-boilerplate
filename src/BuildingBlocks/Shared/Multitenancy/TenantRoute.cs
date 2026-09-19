namespace Boilerplate.BuildingBlocks.Shared.Multitenancy;

/// <summary>
/// The one place the tenant may appear in a URL (ADR-0002): the anonymous,
/// tenant-scoped auth endpoints. Every other request carries its tenant in the
/// signed token, never in the route, header or query string.
/// </summary>
public static class TenantRoute
{
    /// <summary>Route-value key holding the tenant Id on the anonymous auth routes.</summary>
    public const string ValueKey = "tenant";

    /// <summary>
    /// Route template for the anonymous auth group: <c>api/v1/tenants/{tenant}/auth/...</c>.
    /// Endpoints mapped under it must also carry <see cref="TenantFromRouteAttribute"/>.
    /// </summary>
    public const string AnonymousAuthGroup = "api/v{version:apiVersion}/tenants/{" + ValueKey + "}/auth";
}
