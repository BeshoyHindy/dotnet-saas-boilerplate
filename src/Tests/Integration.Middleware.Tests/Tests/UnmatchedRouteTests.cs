using Integration.Middleware.Tests.Infrastructure;

namespace Integration.Middleware.Tests.Tests;

/// <summary>
/// Issue #47: the global <c>FallbackPolicy</c> (see <c>JwtAuthenticationExtensions</c>) is applied by
/// the authorization middleware both to endpoints that decline to declare an intent AND to requests
/// that match no endpoint at all — before this fix both cases answered 401, so an unknown path was
/// indistinguishable from an auth failure. A catch-all endpoint now intercepts truly unmatched
/// requests and answers 404, while a matched endpoint that still forgets to declare an intent keeps
/// hitting FallbackPolicy (401) unchanged.
/// </summary>
[Collection(MiddlewareCollectionDefinition.Name)]
public sealed class UnmatchedRouteTests
{
    private readonly MiddlewareWebApplicationFactory _factory;

    public UnmatchedRouteTests(MiddlewareWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task AnonymousRequest_To_UnknownPath_Should_Return404()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync("/this-path-does-not-exist-anywhere");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task AnonymousRequest_To_UnknownPath_UnderApiPrefix_Should_Return404()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync("/api/v1/does/not/exist");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task AnonymousRequest_To_MappedEndpoint_WithoutAuthMetadata_Should_Return401()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync("/__test/no-metadata");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}
