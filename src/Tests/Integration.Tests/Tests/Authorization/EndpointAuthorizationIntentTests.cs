using Boilerplate.BuildingBlocks.Shared.Identity.Authorization;
using Integration.Tests.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Integration.Tests.Tests.Authorization;

/// <summary>
/// Architecture guard over the endpoints the real host actually maps (read from the running test
/// host's <see cref="EndpointDataSource"/>, so it sees route-group conventions exactly as the
/// authorization middleware does). The RequiredPermission policy fails closed, which makes a missing
/// annotation a 403 in production rather than an open door — this test turns that into a build break
/// at the moment the endpoint is added.
///
/// Every mapped endpoint must declare exactly one intent:
///   • a non-empty <c>.RequirePermission(...)</c> set,
///   • <c>.AllowAnonymous()</c>, or
///   • <c>.RequireAuthenticatedOnly()</c>.
/// </summary>
[Collection(AppCollectionDefinition.Name)]
public sealed class EndpointAuthorizationIntentTests
{
    /// <summary>
    /// Endpoints allowed to carry no intent, by exact display name. Infrastructure the framework maps
    /// for us and that we cannot decorate. The test also fails when an entry stops matching a real
    /// undeclared endpoint, so a fixed entry cannot linger as a stale exemption.
    /// </summary>
    private static readonly string[] KnownUndeclaredEndpoints = [];

    private readonly AppWebApplicationFactory _factory;

    public EndpointAuthorizationIntentTests(AppWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public void Every_Mapped_Endpoint_Should_Declare_Exactly_One_Authorization_Intent()
    {
        var endpoints = MappedEndpoints();

        endpoints.ShouldNotBeEmpty("The host mapped no endpoints — the sweep would be a no-op.");

        var undeclared = endpoints
            .Where(e => IntentsOf(e).Count == 0)
            .Select(Describe)
            .Order(StringComparer.Ordinal)
            .ToArray();

        var unexpected = undeclared
            .Where(name => !KnownUndeclaredEndpoints.Contains(name, StringComparer.Ordinal))
            .ToArray();

        var stale = KnownUndeclaredEndpoints
            .Where(known => !undeclared.Contains(known, StringComparer.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();

        unexpected.ShouldBeEmpty(
            $"{unexpected.Length} endpoint(s) declare no authorization intent: {string.Join("; ", unexpected)}. " +
            "Add .RequirePermission(...), .AllowAnonymous() or .RequireAuthenticatedOnly() — the permission " +
            "policy fails closed, so an undeclared endpoint is dead code for every caller.");

        stale.ShouldBeEmpty(
            $"KnownUndeclaredEndpoints lists endpoints that now declare an intent: {string.Join("; ", stale)}. " +
            "Remove the entry so the allowlist keeps matching reality.");
    }

    [Fact]
    public void No_Mapped_Endpoint_Should_Declare_More_Than_One_Authorization_Intent()
    {
        var conflicting = MappedEndpoints()
            .Where(e => IntentsOf(e).Count > 1)
            .Select(e => $"{Describe(e)} [{string.Join(" + ", IntentsOf(e))}]")
            .Order(StringComparer.Ordinal)
            .ToArray();

        conflicting.ShouldBeEmpty(
            $"{conflicting.Length} endpoint(s) declare conflicting authorization intents: {string.Join("; ", conflicting)}. " +
            "Exactly one of permission / anonymous / authenticated-only must apply, otherwise the effective " +
            "gate depends on metadata ordering rather than on the endpoint's stated intent.");
    }

    private IReadOnlyList<Endpoint> MappedEndpoints()
    {
        return [.. _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints];
    }

    private static List<string> IntentsOf(Endpoint endpoint)
    {
        var intents = new List<string>(3);

        if (endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null)
        {
            intents.Add("AllowAnonymous");
        }

        if (endpoint.Metadata.GetMetadata<IRequiredPermissionMetadata>() is { RequiredPermissions.Count: > 0 })
        {
            intents.Add("RequirePermission");
        }

        if (endpoint.Metadata.GetMetadata<IAuthenticatedOnlyMetadata>() is not null)
        {
            intents.Add("RequireAuthenticatedOnly");
        }

        return intents;
    }

    private static string Describe(Endpoint endpoint)
    {
        return endpoint is RouteEndpoint route
            ? $"{route.RoutePattern.RawText} ({endpoint.DisplayName})"
            : endpoint.DisplayName ?? endpoint.ToString() ?? "<unnamed endpoint>";
    }
}
