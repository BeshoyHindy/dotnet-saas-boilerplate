using System.Security.Claims;
using Boilerplate.BuildingBlocks.Shared.Constants;
using Boilerplate.BuildingBlocks.Shared.Multitenancy;
using Boilerplate.Modules.Multitenancy.Resolution;
using Microsoft.AspNetCore.Http;

namespace Multitenancy.Tests.Resolution;

/// <summary>
/// Pins the ADR-0002 invariant in <see cref="TokenOrRouteTenantStrategy"/>: an authenticated
/// caller is resolved from the <c>tenant</c> claim and nothing else, while the route is only
/// ever consulted for an anonymous caller on an endpoint explicitly marked with
/// <see cref="TenantFromRouteAttribute"/>.
/// </summary>
public sealed class TokenOrRouteTenantStrategyTests
{
    private readonly TokenOrRouteTenantStrategy _sut = new();

    [Fact]
    public async Task GetIdentifierAsync_Should_ReturnClaim_When_AuthenticatedEvenWithMarkedEndpointAndDifferentRouteValue()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.User = AuthenticatedUser("claim-tenant");
        httpContext.Request.RouteValues[TenantRoute.ValueKey] = "route-tenant";
        httpContext.SetEndpoint(new Endpoint(null, new EndpointMetadataCollection(new TenantFromRouteAttribute()), "test"));

        var result = await _sut.GetIdentifierAsync(httpContext);

        result.ShouldBe("claim-tenant");
    }

    [Fact]
    public async Task GetIdentifierAsync_Should_ReturnNull_When_AuthenticatedAndClaimMissing()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.User = AuthenticatedUser(claimValue: null);
        httpContext.Request.RouteValues[TenantRoute.ValueKey] = "route-tenant";
        httpContext.SetEndpoint(new Endpoint(null, new EndpointMetadataCollection(new TenantFromRouteAttribute()), "test"));

        var result = await _sut.GetIdentifierAsync(httpContext);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task GetIdentifierAsync_Should_ReturnNull_When_AuthenticatedAndClaimBlank()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.User = AuthenticatedUser("   ");
        httpContext.Request.RouteValues[TenantRoute.ValueKey] = "route-tenant";
        httpContext.SetEndpoint(new Endpoint(null, new EndpointMetadataCollection(new TenantFromRouteAttribute()), "test"));

        var result = await _sut.GetIdentifierAsync(httpContext);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task GetIdentifierAsync_Should_ReturnRouteValue_When_AnonymousAndEndpointMarked()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity());
        httpContext.Request.RouteValues[TenantRoute.ValueKey] = "route-tenant";
        httpContext.SetEndpoint(new Endpoint(null, new EndpointMetadataCollection(new TenantFromRouteAttribute()), "test"));

        var result = await _sut.GetIdentifierAsync(httpContext);

        result.ShouldBe("route-tenant");
    }

    [Fact]
    public async Task GetIdentifierAsync_Should_ReturnNull_When_AnonymousAndEndpointUnmarked()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity());
        httpContext.Request.RouteValues[TenantRoute.ValueKey] = "route-tenant";
        httpContext.SetEndpoint(new Endpoint(null, new EndpointMetadataCollection(), "test"));

        var result = await _sut.GetIdentifierAsync(httpContext);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task GetIdentifierAsync_Should_ReturnNull_When_AnonymousWithHeaderAndQueryButNoMarkedEndpoint()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity());
        httpContext.Request.Headers["tenant"] = "header-tenant";
        httpContext.Request.QueryString = new QueryString("?tenant=query-tenant");

        var result = await _sut.GetIdentifierAsync(httpContext);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task GetIdentifierAsync_Should_ReturnNull_When_ContextIsNotHttpContext()
    {
        var result = await _sut.GetIdentifierAsync(new object());

        result.ShouldBeNull();
    }

    private static ClaimsPrincipal AuthenticatedUser(string? claimValue)
    {
        var claims = claimValue is null
            ? []
            : new[] { new Claim(ClaimConstants.Tenant, claimValue) };
        var identity = new ClaimsIdentity(claims, "Bearer");
        return new ClaimsPrincipal(identity);
    }
}
