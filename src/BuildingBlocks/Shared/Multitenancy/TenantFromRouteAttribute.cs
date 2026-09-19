namespace Boilerplate.BuildingBlocks.Shared.Multitenancy;

/// <summary>
/// Endpoint metadata that opts an endpoint into reading the tenant from the
/// <c>{tenant}</c> route value — and only while the caller is anonymous.
///
/// Tenant resolution ignores the route unless this marker is present, so a route
/// parameter named "tenant" on any other endpoint (for example the tenant
/// administration endpoints under <c>api/v1/tenants/{id}</c>) can never steer the
/// ambient tenant. Once a caller is authenticated the token claim is the only
/// input and this marker is ignored.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class TenantFromRouteAttribute : Attribute;
