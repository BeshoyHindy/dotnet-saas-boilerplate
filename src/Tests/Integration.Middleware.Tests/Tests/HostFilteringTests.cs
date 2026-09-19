using Integration.Middleware.Tests.Infrastructure;

namespace Integration.Middleware.Tests.Tests;

/// <summary>
/// Exercises ASP.NET Core's host filtering, which is driven by the <c>AllowedHosts</c> configuration
/// key. The factory pins <c>AllowedHosts</c> to an explicit list (never <c>*</c>), so a request whose
/// Host header names another authority must be rejected with 400 before it reaches any endpoint —
/// the protection that stops Host-header poisoning behind a reverse proxy.
/// </summary>
[Collection(MiddlewareCollectionDefinition.Name)]
public sealed class HostFilteringTests
{
    private readonly MiddlewareWebApplicationFactory _factory;

    public HostFilteringTests(MiddlewareWebApplicationFactory factory)
    {
        _factory = factory;
    }

    #region Happy Path

    [Fact]
    public async Task RootEndpoint_Should_BeServed_When_HostIsAllowed()
    {
        // Arrange
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Host = TestConstants.AllowedHost;

        // Act
        using var response = await client.SendAsync(request);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    #endregion

    #region Exception

    [Fact]
    public async Task RootEndpoint_Should_Return400_When_HostIsNotAllowed()
    {
        // Arrange
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Host = "evil.example.com";

        // Act
        using var response = await client.SendAsync(request);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    #endregion
}
