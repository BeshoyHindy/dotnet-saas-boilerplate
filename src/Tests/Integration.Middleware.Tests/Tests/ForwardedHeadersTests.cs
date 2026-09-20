using Integration.Middleware.Tests.Infrastructure;

namespace Integration.Middleware.Tests.Tests;

/// <summary>
/// Exercises the reverse-proxy forwarded headers. The factory enables ProxyOptions with
/// TrustAnyProxy, so <c>UseForwardedHeaders</c> really runs and the test can tell apart the headers
/// the app honours from the ones it refuses.
/// </summary>
/// <remarks>
/// <c>X-Forwarded-Host</c> is refused on purpose. Host filtering runs before forwarded headers, so
/// honouring it would let a caller swap <c>Request.Host</c> after the allow-list approved the real
/// one. The Identity module's mailed links are additionally built from <c>OriginOptions</c> rather
/// than the request, so a swapped host is not an account-takeover primitive on that path either.
/// </remarks>
[Collection(MiddlewareCollectionDefinition.Name)]
public sealed class ForwardedHeadersTests
{
    private readonly MiddlewareWebApplicationFactory _factory;

    public ForwardedHeadersTests(MiddlewareWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private static async Task<RequestInfo> GetRequestInfoAsync(HttpClient client, Action<HttpRequestMessage> configure)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/__test/request-info");
        configure(request);

        using var response = await client.SendAsync(request);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var info = await response.Content.ReadFromJsonAsync<RequestInfo>();
        info.ShouldNotBeNull();
        return info;
    }

    #region Happy Path

    [Fact]
    public async Task RequestInfo_Should_UseTheForwardedScheme_When_TheProxyReportsHttps()
    {
        // Arrange
        using var client = _factory.CreateClient();

        // Act
        var info = await GetRequestInfoAsync(client, request => request.Headers.Add("X-Forwarded-Proto", "https"));

        // Assert — proves the forwarded-headers middleware is active in this host.
        info.Scheme.ShouldBe("https");
    }

    #endregion

    #region Exception

    [Fact]
    public async Task RequestInfo_Should_KeepTheRealHost_When_AForeignForwardedHostIsSent()
    {
        // Arrange
        using var client = _factory.CreateClient();

        // Act
        var info = await GetRequestInfoAsync(client, request =>
        {
            request.Headers.Host = TestConstants.AllowedHost;
            request.Headers.Add("X-Forwarded-Host", "evil.example.com");
        });

        // Assert
        info.Host.ShouldNotContain("evil.example.com");
        info.Host.ShouldStartWith(TestConstants.AllowedHost);
    }

    [Fact]
    public async Task RequestInfo_Should_KeepTheRealHost_When_ForwardedHostAndProtoArriveTogether()
    {
        // Arrange — a realistic spoof attempt rides along with the headers a proxy legitimately sets.
        using var client = _factory.CreateClient();

        // Act
        var info = await GetRequestInfoAsync(client, request =>
        {
            request.Headers.Host = TestConstants.AllowedHost;
            request.Headers.Add("X-Forwarded-Proto", "https");
            request.Headers.Add("X-Forwarded-For", "203.0.113.7");
            request.Headers.Add("X-Forwarded-Host", "evil.example.com");
        });

        // Assert
        info.Scheme.ShouldBe("https");
        info.Host.ShouldNotContain("evil.example.com");
    }

    #endregion
}
