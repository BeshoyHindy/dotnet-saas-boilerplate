using Boilerplate.BuildingBlocks.Web.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;

namespace Framework.Tests.Web;

public sealed class SecurityHeadersMiddlewareTests
{
    /// <summary>
    /// The middleware writes its headers from an <c>OnStarting</c> callback, and
    /// <see cref="DefaultHttpContext"/>'s stock response feature drops those callbacks on the floor.
    /// This one records them so a test can start the response and observe the headers.
    /// </summary>
    private sealed class RecordingResponseFeature : HttpResponseFeature
    {
        private readonly List<(Func<object, Task> Callback, object State)> _callbacks = [];

        public override void OnStarting(Func<object, Task> callback, object state) => _callbacks.Add((callback, state));

        public async Task StartResponseAsync()
        {
            foreach (var (callback, state) in _callbacks)
            {
                await callback(state);
            }
        }
    }

    private static DefaultHttpContext CreateContext(string path, bool https, out RecordingResponseFeature response)
    {
        var context = new DefaultHttpContext();
        response = new RecordingResponseFeature();
        context.Features.Set<IHttpResponseFeature>(response);
        context.Request.Path = path;
        if (https)
        {
            context.Request.Scheme = "https";
        }

        return context;
    }

    private static async Task<HttpContext> InvokeAsync(SecurityHeadersOptions options, string path = "/api/v1/users", bool https = false)
    {
        var context = CreateContext(path, https, out var response);

        var nextInvoked = false;
        var middleware = new SecurityHeadersMiddleware(
            _ => { nextInvoked = true; return Task.CompletedTask; },
            Options.Create(options));

        await middleware.InvokeAsync(context);
        await response.StartResponseAsync();
        context.Items["__nextInvoked"] = nextInvoked;
        return context;
    }

    #region Happy Path

    [Fact]
    public async Task InvokeAsync_Should_SetBaseHeaders_When_Enabled()
    {
        // Act
        var context = await InvokeAsync(new SecurityHeadersOptions());
        var headers = context.Response.Headers;

        // Assert
        headers["X-Content-Type-Options"].ToString().ShouldBe("nosniff");
        headers["X-Frame-Options"].ToString().ShouldBe("DENY");
        headers["Referrer-Policy"].ToString().ShouldBe("strict-origin-when-cross-origin");
        headers["X-XSS-Protection"].ToString().ShouldBe("0");
        headers["Content-Security-Policy"].ToString().ShouldContain("default-src 'self'");
        ((bool)context.Items["__nextInvoked"]!).ShouldBeTrue();
    }

    [Fact]
    public async Task InvokeAsync_Should_AddHsts_When_RequestIsHttps()
    {
        // Act
        var context = await InvokeAsync(new SecurityHeadersOptions(), https: true);

        // Assert
        context.Response.Headers["Strict-Transport-Security"].ToString()
            .ShouldBe("max-age=31536000; includeSubDomains");
    }

    [Fact]
    public async Task InvokeAsync_Should_OmitHsts_When_RequestIsHttp()
    {
        // Act
        var context = await InvokeAsync(new SecurityHeadersOptions(), https: false);

        // Assert
        context.Response.Headers.ContainsKey("Strict-Transport-Security").ShouldBeFalse();
    }

    [Fact]
    public async Task InvokeAsync_Should_IncludeUnsafeInlineStyles_When_AllowInlineStylesTrue()
    {
        // Act
        var context = await InvokeAsync(new SecurityHeadersOptions { AllowInlineStyles = true });

        // Assert
        context.Response.Headers["Content-Security-Policy"].ToString().ShouldContain("'unsafe-inline'");
    }

    [Fact]
    public async Task InvokeAsync_Should_OmitUnsafeInlineStyles_When_AllowInlineStylesFalse()
    {
        // Act
        var context = await InvokeAsync(new SecurityHeadersOptions { AllowInlineStyles = false });
        var csp = context.Response.Headers["Content-Security-Policy"].ToString();

        // Assert — style-src present but without unsafe-inline
        csp.ShouldContain("style-src 'self'");
        csp.ShouldNotContain("'unsafe-inline'");
    }

    [Fact]
    public async Task InvokeAsync_Should_AppendCustomSources_When_Provided()
    {
        // Arrange
        var options = new SecurityHeadersOptions
        {
            ScriptSources = ["https://cdn.example.com"],
            StyleSources = ["https://styles.example.com"]
        };

        // Act
        var context = await InvokeAsync(options);
        var csp = context.Response.Headers["Content-Security-Policy"].ToString();

        // Assert
        csp.ShouldContain("https://cdn.example.com");
        csp.ShouldContain("https://styles.example.com");
    }

    #endregion

    #region Edge Cases

    [Fact]
    public async Task InvokeAsync_Should_SkipHeaders_When_Disabled()
    {
        // Act
        var context = await InvokeAsync(new SecurityHeadersOptions { Enabled = false });

        // Assert
        context.Response.Headers.ContainsKey("X-Content-Type-Options").ShouldBeFalse();
        context.Response.Headers.ContainsKey("Content-Security-Policy").ShouldBeFalse();
        ((bool)context.Items["__nextInvoked"]!).ShouldBeTrue();
    }

    [Fact]
    public async Task InvokeAsync_Should_SkipHeaders_When_PathExcluded()
    {
        // Act
        var context = await InvokeAsync(new SecurityHeadersOptions(), path: "/scalar/index.html");

        // Assert
        context.Response.Headers.ContainsKey("X-Content-Type-Options").ShouldBeFalse();
        ((bool)context.Items["__nextInvoked"]!).ShouldBeTrue();
    }

    [Fact]
    public async Task InvokeAsync_Should_NotOverwriteCsp_When_AlreadyPresent()
    {
        // Arrange
        var context = CreateContext("/api", https: false, out var response);
        context.Response.Headers["Content-Security-Policy"] = "default-src 'none'";
        var middleware = new SecurityHeadersMiddleware(_ => Task.CompletedTask, Options.Create(new SecurityHeadersOptions()));

        // Act
        await middleware.InvokeAsync(context);
        await response.StartResponseAsync();

        // Assert
        context.Response.Headers["Content-Security-Policy"].ToString().ShouldBe("default-src 'none'");
    }

    [Fact]
    public async Task InvokeAsync_Should_SetHeadersAtResponseStart_When_TheResponseIsResetAfterTheMiddlewareRan()
    {
        // Arrange — exactly what UseExceptionHandler does before re-running the handler.
        var context = CreateContext("/api", https: false, out var response);
        var middleware = new SecurityHeadersMiddleware(_ => Task.CompletedTask, Options.Create(new SecurityHeadersOptions()));

        // Act
        await middleware.InvokeAsync(context);
        context.Response.Headers.Clear();
        await response.StartResponseAsync();

        // Assert
        context.Response.Headers["X-Content-Type-Options"].ToString().ShouldBe("nosniff");
        context.Response.Headers["Content-Security-Policy"].ToString().ShouldContain("default-src 'self'");
    }

    #endregion
}
