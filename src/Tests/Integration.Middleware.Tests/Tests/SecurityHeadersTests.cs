using Integration.Middleware.Tests.Infrastructure;

namespace Integration.Middleware.Tests.Tests;

/// <summary>
/// Exercises the production <see cref="Boilerplate.BuildingBlocks.Web.Security.SecurityHeadersMiddleware"/>.
/// The factory enables SecurityHeadersOptions, so a request to a non-excluded path must carry the
/// standard hardening headers.
/// </summary>
[Collection(MiddlewareCollectionDefinition.Name)]
public sealed class SecurityHeadersTests
{
    private readonly MiddlewareWebApplicationFactory _factory;

    public SecurityHeadersTests(MiddlewareWebApplicationFactory factory)
    {
        _factory = factory;
    }

    #region Happy Path

    [Fact]
    public async Task RootEndpoint_Should_EmitSecurityHeaders_When_SecurityHeadersAreEnabled()
    {
        // Arrange
        using var client = _factory.CreateClient();

        // Act
        using var response = await client.GetAsync("/");
        var headers = response.Headers;

        // Assert
        headers.TryGetValues("X-Content-Type-Options", out var contentTypeOptions).ShouldBeTrue();
        contentTypeOptions!.ShouldContain("nosniff");

        headers.TryGetValues("X-Frame-Options", out var frameOptions).ShouldBeTrue();
        frameOptions!.ShouldNotBeEmpty();

        headers.TryGetValues("Referrer-Policy", out var referrerPolicy).ShouldBeTrue();
        referrerPolicy!.ShouldNotBeEmpty();

        headers.TryGetValues("Content-Security-Policy", out var csp).ShouldBeTrue();
        csp!.ShouldNotBeEmpty();
    }

    #endregion

    #region Exception

    [Fact]
    public async Task ThrowEndpoint_Should_StillEmitSecurityHeaders_When_TheExceptionHandlerWritesTheResponse()
    {
        // Arrange
        using var client = _factory.CreateClient();

        // Act: UseExceptionHandler clears the response (status, body AND headers) before re-running
        // the pipeline, so headers written eagerly on the way in are lost on the 500 path.
        using var response = await client.GetAsync("/__test/throw");
        var headers = response.Headers;

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);

        headers.TryGetValues("X-Content-Type-Options", out var contentTypeOptions).ShouldBeTrue(
            "hardening headers must survive the global exception handler");
        contentTypeOptions!.ShouldContain("nosniff");

        headers.TryGetValues("X-Frame-Options", out var frameOptions).ShouldBeTrue();
        frameOptions!.ShouldNotBeEmpty();

        headers.TryGetValues("Referrer-Policy", out var referrerPolicy).ShouldBeTrue();
        referrerPolicy!.ShouldNotBeEmpty();

        headers.TryGetValues("Content-Security-Policy", out var csp).ShouldBeTrue();
        csp!.ShouldNotBeEmpty();
    }

    #endregion
}
