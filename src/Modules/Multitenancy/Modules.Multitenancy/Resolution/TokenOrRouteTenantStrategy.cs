using Boilerplate.BuildingBlocks.Shared.Constants;
using Boilerplate.BuildingBlocks.Shared.Multitenancy;
using Finbuckle.MultiTenant.Abstractions;
using Microsoft.AspNetCore.Http;
using System.Security.Claims;

namespace Boilerplate.Modules.Multitenancy.Resolution;

/// <summary>
/// The only tenant resolution strategy in the system (ADR-0002).
///
/// <list type="bullet">
/// <item>Authenticated caller → the <c>tenant</c> claim of the signed token, and nothing else.
/// The route, headers and query string are never consulted, so a forged <c>tenant</c> header
/// or <c>?tenant=</c> has no effect at all — there is nothing to validate because the caller
/// cannot name a tenant.</item>
/// <item>Anonymous caller → the <c>{tenant}</c> route value, but only on an endpoint explicitly
/// marked with <see cref="TenantFromRouteAttribute"/> (the <c>api/v1/tenants/{tenant}/auth/...</c>
/// group). This is what lets login, refresh and the password/confirmation flows reach a tenant
/// before a token exists.</item>
/// <item>Anything else → no tenant.</item>
/// </list>
///
/// Requires <c>UseAuthentication()</c> and <c>UseRouting()</c> to have run first; the pipeline
/// installs <c>UseMultiTenant()</c> from <see cref="MultitenancyModule.ConfigureMiddleware"/>,
/// which runs after both.
/// </summary>
public sealed class TokenOrRouteTenantStrategy : IMultiTenantStrategy
{
    public Task<string?> GetIdentifierAsync(object context)
    {
        if (context is not HttpContext httpContext)
        {
            return Task.FromResult<string?>(null);
        }

        if (httpContext.User?.Identity?.IsAuthenticated == true)
        {
            return Task.FromResult(NullIfBlank(httpContext.User.FindFirstValue(ClaimConstants.Tenant)));
        }

        var endpoint = httpContext.GetEndpoint();
        if (endpoint?.Metadata.GetMetadata<TenantFromRouteAttribute>() is null)
        {
            return Task.FromResult<string?>(null);
        }

        httpContext.Request.RouteValues.TryGetValue(TenantRoute.ValueKey, out var routeValue);
        return Task.FromResult(NullIfBlank(routeValue?.ToString()));
    }

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
