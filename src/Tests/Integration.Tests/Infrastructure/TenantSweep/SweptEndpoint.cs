using Boilerplate.BuildingBlocks.Shared.Constants;
using Boilerplate.BuildingBlocks.Shared.Identity.Authorization;
using Boilerplate.BuildingBlocks.Shared.Multitenancy;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;

namespace Integration.Tests.Infrastructure.TenantSweep;

/// <summary>
/// How the sweep treats one discovered route.
/// </summary>
public enum SweepClass
{
    /// <summary>Carries at least one resource route parameter — sweepable with another tenant's id.</summary>
    Resource,

    /// <summary>A GET with no resource parameter — a collection that must not contain another tenant's rows.</summary>
    Collection,

    /// <summary>Everything else: writes and RPC-style posts that name no resource.</summary>
    Other,
}

/// <summary>
/// One route as the running host actually published it, plus everything the sweep needs to decide
/// what to do with it. Built from <see cref="EndpointDataSource"/> so the sweep is a statement about
/// the app that ships, not about a list someone remembered to update.
/// </summary>
public sealed record SweptEndpoint(
    string Method,
    string Template,
    SweepClass Class,
    IReadOnlyList<ResourceParameter> ResourceParameters,
    IReadOnlyCollection<string> RequiredPermissions,
    bool IsRootOnly,
    bool IsVersionedApi,
    string? ExemptReason)
{
    /// <summary>Readable name used in failure output, e.g. <c>GET api/v1/files/{id:guid}</c>.</summary>
    public string Name => $"{Method} {Template}";

    public bool IsExempt => ExemptReason is not null;
}

/// <summary>
/// A route parameter that names a resource, paired with the registry key the sweep resolves it
/// against: the literal segment immediately before it plus the parameter's own name. Keying on the
/// pair rather than the whole template is what makes a <i>new</i> endpoint covered for free —
/// <c>GET files/{id}/thumbnail</c> resolves through the same <c>(files, id)</c> entry as
/// <c>GET files/{id}</c>.
/// </summary>
/// <param name="Name">The route parameter name, e.g. <c>id</c>.</param>
/// <param name="PrecedingSegment">The literal segment before it, e.g. <c>files</c>; empty if none.</param>
public sealed record ResourceParameter(string Name, string PrecedingSegment)
{
    public string RegistryKey => $"{PrecedingSegment}/{Name}";
}

public static class EndpointSweepDiscovery
{
    /// <summary>
    /// Route parameters that never name a resource: the API version segment, and the
    /// <c>{tenant}</c> of the anonymous auth group (ADR-0002's one sanctioned tenant-in-URL, whose
    /// cross-tenant behaviour is already proven by the refresh-token and login suites).
    /// </summary>
    private static readonly string[] NonResourceParameters = ["version", TenantRoute.ValueKey];

    /// <summary>Prefix of the versioned API surface; everything else is infrastructure.</summary>
    private const string VersionedApiPrefix = "api/v{version:apiVersion}/";

    public static IReadOnlyList<SweptEndpoint> Discover(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        var permissions = services.GetRequiredService<IPermissionRegistry>();
        var rootPermissionNames = permissions.Root.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        return [.. services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .SelectMany(endpoint => Expand(endpoint, rootPermissionNames))
            .OrderBy(e => e.Template, StringComparer.Ordinal)
            .ThenBy(e => e.Method, StringComparer.Ordinal)];
    }

    /// <summary>
    /// One <see cref="RouteEndpoint"/> can answer several verbs; the sweep treats each verb as its
    /// own case, because DELETE and GET on the same template fail differently.
    /// </summary>
    private static IEnumerable<SweptEndpoint> Expand(RouteEndpoint endpoint, HashSet<string> rootPermissionNames)
    {
        var template = endpoint.RoutePattern.RawText ?? string.Empty;
        var resourceParameters = ResourceParametersOf(endpoint.RoutePattern);
        var required = endpoint.Metadata.GetOrderedMetadata<IRequiredPermissionMetadata>()
            .SelectMany(m => m.RequiredPermissions)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var exemptReason = endpoint.Metadata.GetMetadata<TenantSweepExemptAttribute>()?.Reason;
        bool isVersionedApi = template.StartsWith(VersionedApiPrefix, StringComparison.Ordinal);

        // Root-only is read off permission metadata, not off a list: a permission flagged IsRoot in
        // the catalog is one a tenant admin can never hold, so 403 is the correct answer there and
        // the sweep must not read it as a leak.
        bool isRootOnly = required.Count > 0 && required.Any(rootPermissionNames.Contains);

        var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods;
        var effectiveMethods = methods is { Count: > 0 } ? methods : ["*"];

        foreach (var method in effectiveMethods)
        {
            var sweepClass = SweepClass.Other;
            if (resourceParameters.Count > 0)
            {
                sweepClass = SweepClass.Resource;
            }
            else if (string.Equals(method, HttpMethods.Get, StringComparison.OrdinalIgnoreCase))
            {
                sweepClass = SweepClass.Collection;
            }

            yield return new SweptEndpoint(
                method,
                template,
                sweepClass,
                resourceParameters,
                required,
                isRootOnly,
                isVersionedApi,
                exemptReason);
        }
    }

    private static List<ResourceParameter> ResourceParametersOf(RoutePattern pattern)
    {
        var result = new List<ResourceParameter>();
        string preceding = string.Empty;

        foreach (var segment in pattern.PathSegments)
        {
            foreach (var part in segment.Parts)
            {
                if (part is RoutePatternLiteralPart literal)
                {
                    preceding = literal.Content;
                    continue;
                }

                if (part is not RoutePatternParameterPart parameter)
                {
                    continue;
                }

                if (!NonResourceParameters.Contains(parameter.Name, StringComparer.Ordinal))
                {
                    result.Add(new ResourceParameter(parameter.Name, preceding));
                }
            }
        }

        return result;
    }
}
