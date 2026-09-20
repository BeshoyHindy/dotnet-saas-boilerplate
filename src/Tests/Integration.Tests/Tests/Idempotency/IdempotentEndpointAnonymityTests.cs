using Boilerplate.BuildingBlocks.Web.Idempotency;
using Integration.Tests.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Integration.Tests.Tests.Idempotency;

/// <summary>
/// The authority on "no anonymous route is idempotent" (#84), and the reason it exists rather than
/// relying on <c>AnonymousRoutesAreNeverIdempotentTests</c> (Architecture.Tests) alone: this repository
/// declares anonymity three ways — a route-group <c>.AllowAnonymous()</c>
/// (<c>TenantRoute.AnonymousAuthGroup</c> in <c>IdentityModule.MapEndpoints</c>), a handler-level
/// <c>[AllowAnonymous]</c> attribute (<c>GenerateTokenEndpoint</c>, <c>RefreshTokenEndpoint</c>,
/// <c>EndSessionEndpoint</c>), and a chain call on the route itself. Only the third form appears in the
/// text of the file that maps the route, so a source-text scan is structurally blind to the first two.
///
/// <para>This test instead reads the running host's <see cref="EndpointDataSource"/> — the same
/// merged, built metadata the authorization middleware itself reads — the way
/// <c>EndpointAuthorizationIntentTests</c> does for authorization intent. Anonymity declared any of
/// the three ways surfaces identically as <see cref="IAllowAnonymous"/> metadata on the endpoint, and
/// <c>.WithIdempotency()</c> always attaches <see cref="IdempotentEndpointMetadata"/> alongside its
/// filter, so both facts are read from the one place that cannot be fooled by where in the source the
/// declaration happened to be written.</para>
/// </summary>
[Collection(AppCollectionDefinition.Name)]
public sealed class IdempotentEndpointAnonymityTests
{
    private readonly AppWebApplicationFactory _factory;

    public IdempotentEndpointAnonymityTests(AppWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public void No_Endpoint_Should_Be_Both_Anonymous_And_Idempotent()
    {
        var endpoints = MappedEndpoints();

        endpoints.ShouldNotBeEmpty("The host mapped no endpoints — the sweep would be a no-op.");

        // Anti-vacuous: a scanner that stopped finding either kind of endpoint would pass in silence.
        endpoints.Any(IsIdempotent).ShouldBeTrue("the sweep should be seeing at least one idempotent endpoint.");
        endpoints.Any(IsAnonymous).ShouldBeTrue("the sweep should be seeing at least one anonymous endpoint.");

        var offenders = endpoints
            .Where(e => IsAnonymous(e) && IsIdempotent(e))
            .Select(Describe)
            .Order(StringComparer.Ordinal)
            .ToArray();

        offenders.ShouldBeEmpty(
            "an anonymous caller has no subject to bind the idempotency partition to, so the caller-" +
            "supplied key alone would hand a guessed key someone else's stored response, however the " +
            "route declared its anonymity (route group, [AllowAnonymous], or chain call). Offenders: " +
            string.Join("; ", offenders));
    }

    private IReadOnlyList<Endpoint> MappedEndpoints()
    {
        return [.. _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints];
    }

    private static bool IsAnonymous(Endpoint endpoint) =>
        endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null;

    private static bool IsIdempotent(Endpoint endpoint) =>
        endpoint.Metadata.GetMetadata<IIdempotentEndpointMetadata>() is not null;

    private static string Describe(Endpoint endpoint) =>
        endpoint is RouteEndpoint route
            ? $"{route.RoutePattern.RawText} ({endpoint.DisplayName})"
            : endpoint.DisplayName ?? endpoint.ToString() ?? "<unnamed endpoint>";
}
