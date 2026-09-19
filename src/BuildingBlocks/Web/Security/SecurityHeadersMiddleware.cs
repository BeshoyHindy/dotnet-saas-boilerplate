using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Boilerplate.BuildingBlocks.Web.Security;

public sealed class SecurityHeadersMiddleware(RequestDelegate next, IOptions<SecurityHeadersOptions> options)
{
    private readonly SecurityHeadersOptions _options = options.Value;

    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!_options.Enabled)
        {
            return next(context);
        }

        var path = context.Request.Path;

        // Allow listed paths (e.g., OpenAPI / Scalar UI) to manage their own scripts/styles.
        if (_options.ExcludedPaths?.Any(p => path.StartsWithSegments(p, StringComparison.OrdinalIgnoreCase)) == true)
        {
            return next(context);
        }

        // Write the headers from an OnStarting callback instead of eagerly on the way in:
        // UseExceptionHandler resets the response (status, body AND headers) before it re-runs the
        // handler, so eagerly written headers are silently dropped from every 5xx it produces —
        // exactly the responses an attacker can most easily provoke. OnStarting registrations
        // survive that reset and fire once, immediately before the response is flushed.
        context.Response.OnStarting(static state =>
        {
            var (middleware, httpContext) = ((SecurityHeadersMiddleware, HttpContext))state;
            middleware.ApplyHeaders(httpContext);
            return Task.CompletedTask;
        }, (this, context));

        return next(context);
    }

    private void ApplyHeaders(HttpContext context)
    {
        var headers = context.Response.Headers;

        headers["X-Content-Type-Options"] = "nosniff";
        headers["X-Frame-Options"] = "DENY";
        headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
        headers["X-XSS-Protection"] = "0";

        if (context.Request.IsHttps)
        {
            headers["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains";
        }

        if (!headers.ContainsKey("Content-Security-Policy"))
        {
            var scriptSources = string.Join(' ', _options.ScriptSources ?? []);
            var styleSources = string.Join(' ', _options.StyleSources ?? []);

            var csp =
                "default-src 'self'; " +
                "img-src 'self' data: https:; " +
                $"script-src 'self' https: {scriptSources}; " +
                $"style-src 'self' {(_options.AllowInlineStyles ? "'unsafe-inline' " : string.Empty)}{styleSources}; " +
                "object-src 'none'; " +
                "frame-ancestors 'none'; " +
                "base-uri 'self';";

            headers["Content-Security-Policy"] = csp;
        }
    }
}