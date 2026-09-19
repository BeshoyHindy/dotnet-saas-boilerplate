using Boilerplate.BuildingBlocks.Shared.Multitenancy;
using Microsoft.AspNetCore.Http;
using System.Globalization;

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
/// </summary>
internal static class RefreshTokenCookie
{
    public const string Name = "refresh_token";

    /// <summary>
    /// The refresh path for a tenant, matching <see cref="TenantRoute.AnonymousAuthGroup"/> with the
    /// version segment resolved. Cookie paths are literal, so the template cannot be reused as-is.
    /// </summary>
    public static string PathFor(string tenantId) =>
        string.Create(CultureInfo.InvariantCulture, $"/api/v1/tenants/{tenantId}/auth/refresh");

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

    public static string? Read(HttpContext httpContext) =>
        httpContext.Request.Cookies.TryGetValue(Name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;
}
