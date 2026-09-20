using Boilerplate.BuildingBlocks.Shared.Multitenancy;
using Microsoft.AspNetCore.Http;
using System.Globalization;
using System.Text.RegularExpressions;

// Deliberately outside `.Features.`: it touches HttpContext, which the layer-dependency
// architecture test bans for anything in a feature folder that is not an endpoint.
namespace Boilerplate.Modules.Identity;

/// <summary>
/// Browser delivery of the refresh token (ADR-0002). A browser cannot be trusted to keep a
/// bearer secret out of JavaScript's reach, so it gets the token as a cookie it cannot read:
/// <c>HttpOnly; Secure; SameSite=Strict</c>, scoped by <c>Path</c> to the one endpoint that
/// consumes it, so no other request ever carries it.
///
/// Non-browser clients keep using the response body — the cookie is additive, and since CORS
/// no longer allows credentials (#13) a cross-origin SPA simply never receives or sends it.
///
/// <c>Secure</c> means browsers drop the cookie on plain http from anything but localhost, so a
/// Development host reached by LAN address or hostname never stores it; body delivery covers that
/// case and is what both clients actually use.
/// </summary>
internal static partial class RefreshTokenCookie
{
    public const string Name = "refresh_token";

    /// <summary>
    /// The refresh path for a tenant, matching <see cref="TenantRoute.AnonymousAuthGroup"/> with the
    /// version segment resolved. Cookie paths are literal, so the template cannot be reused as-is.
    ///
    /// The tenant Id is interpolated straight in. It is safe because
    /// <c>CreateTenantCommandValidator</c> restricts it to a lowercase slug, and this asserts that
    /// invariant at the sink rather than trusting it: a tenant Id carrying <c>;</c> or a newline
    /// would otherwise let the Path escape into another cookie attribute or a second header.
    /// </summary>
    public static string PathFor(string tenantId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        if (!TenantIdSlug.IsMatch(tenantId))
        {
            throw new InvalidOperationException(
                $"Tenant Id '{tenantId}' is not a slug and cannot be placed in a cookie path.");
        }

        return string.Create(CultureInfo.InvariantCulture, $"/api/v1/tenants/{tenantId}/auth/refresh");
    }

    public static void Append(HttpContext httpContext, string tenantId, string refreshToken, DateTime expiresAtUtc)
    {
        httpContext.Response.Cookies.Append(Name, refreshToken, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Path = PathFor(tenantId),
            Expires = new DateTimeOffset(DateTime.SpecifyKind(expiresAtUtc, DateTimeKind.Utc)),
            IsEssential = true,
        });
    }

    /// <summary>
    /// Clears the cookie. Every attribute must match <see cref="Append"/> — a browser matches a
    /// deletion to the cookie it replaces by name, Path and Domain, so a mismatched Path silently
    /// leaves the credential in place, which is exactly the bug this exists to prevent.
    /// </summary>
    public static void Delete(HttpContext httpContext, string tenantId)
    {
        httpContext.Response.Cookies.Delete(Name, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Path = PathFor(tenantId),
            IsEssential = true,
        });
    }

    /// <summary>
    /// Clears the cookie when the response starts writing, unless the response is by then carrying
    /// a fresh <see cref="Name"/> cookie of its own.
    ///
    /// Two reasons it is deferred rather than written eagerly. First, <c>UseExceptionHandler</c>
    /// resets status, body *and* headers before re-running the pipeline, so an eager Set-Cookie
    /// vanishes from exactly the responses that most need it — the 401s (the security headers use
    /// <c>OnStarting</c> for the same reason; see <c>security.md</c>). Second, deferring lets a
    /// successful rotation simply win: it appends the new cookie, and this then finds it and stands
    /// down, so "clear on every outcome except a successful rotation" needs no flag passed around.
    /// </summary>
    public static void DeleteWhenResponseStarts(HttpContext httpContext, string tenantId)
    {
        httpContext.Response.OnStarting(static state =>
        {
            var (context, tenant) = ((HttpContext, string))state;

            var alreadySet = context.Response.Headers.SetCookie
                .Any(value => value?.StartsWith(Name + "=", StringComparison.Ordinal) == true);

            if (!alreadySet)
            {
                Delete(context, tenant);
            }

            return Task.CompletedTask;
        }, (httpContext, tenantId));
    }

    public static string? Read(HttpContext httpContext) =>
        httpContext.Request.Cookies.TryGetValue(Name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;

    private static readonly Regex TenantIdSlug = BuildTenantIdSlug();

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{1,62}$", RegexOptions.CultureInvariant)]
    private static partial Regex BuildTenantIdSlug();
}
