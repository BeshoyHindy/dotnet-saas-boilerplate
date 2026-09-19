using Microsoft.AspNetCore.Builder;

namespace Boilerplate.BuildingBlocks.Shared.Multitenancy;

/// <summary>
/// Endpoint metadata that excuses an endpoint from the cross-tenant sweep in
/// <c>Integration.Tests</c>, which calls every route carrying a resource id with tenant A's token
/// and tenant B's id and demands a 404.
///
/// The sweep enumerates <c>EndpointDataSource</c>, so a newly added endpoint is covered the day it
/// is mapped: if the sweep cannot find a seeded resource for it, the test fails and names the
/// endpoint. That is deliberate — the only way to be left out is to say so here, in the route
/// definition, with a reason a reviewer can read on the diff.
///
/// This is <b>not</b> a way to silence a genuine leak. An endpoint that answers 200 or 403 for
/// another tenant's id is a bug in the endpoint; exempt only routes whose id parameter is not a
/// tenant-scoped resource at all (a version segment, an opaque token, a platform-wide id).
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class TenantSweepExemptAttribute : Attribute
{
    /// <param name="reason">
    /// Why this route's id is not a tenant-scoped resource. Mandatory: the sweep prints it in the
    /// exempt list, so "why" travels with the test output rather than living only in a commit.
    /// </param>
    public TenantSweepExemptAttribute(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        Reason = reason;
    }

    /// <summary>Why this route's id is not a tenant-scoped resource.</summary>
    public string Reason { get; }
}

/// <summary>
/// Route-builder sugar for <see cref="TenantSweepExemptAttribute"/>, so an exemption reads in the
/// same voice as <c>.RequirePermission(...)</c> at the mapping site.
/// </summary>
public static class TenantSweepEndpointExtensions
{
    /// <inheritdoc cref="TenantSweepExemptAttribute"/>
    /// <param name="builder">The endpoint being mapped.</param>
    /// <param name="reason">Why this route's id is not a tenant-scoped resource.</param>
    public static TBuilder ExemptFromTenantSweep<TBuilder>(this TBuilder builder, string reason)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.WithMetadata(new TenantSweepExemptAttribute(reason));
    }
}
