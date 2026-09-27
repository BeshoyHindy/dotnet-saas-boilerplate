using Microsoft.AspNetCore.Http;

// Deliberately outside `.Features.`: it touches HttpContext, which the layer-dependency
// architecture test bans for anything in a feature folder that is not an endpoint.
namespace Boilerplate.Modules.Identity;

/// <summary>
/// Browser delivery of Job monitor access (ADR-0009). The Job monitor's pages, assets and polling
/// cannot carry a bearer header, so a root operator's browser gets a short-lived credential it cannot
/// read instead: <c>HttpOnly; Secure; SameSite=Strict</c>, no <c>Domain</c> (host-only), and
/// <c>Path</c> pinned to the Job monitor route, so no other request ever carries it.
///
/// The value is a signed token whose audience only the Job monitor cookie scheme accepts
/// (<see cref="Authorization.Jwt.JobMonitorCookieAuthentication"/>), carrying the operator's
/// <c>sid</c>; it is checked against the session on every request. Its life is fixed — nothing
/// slides it — and the console asks for a fresh one each time the operator opens the Job monitor.
/// </summary>
internal static class JobMonitorCookie
{
    public const string Name = "__Secure-job_monitor";

    /// <summary>How long one issued cookie lasts. Fixed; never extended by use.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    public static void Append(HttpContext httpContext, string path, string token, DateTime expiresAtUtc)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        httpContext.Response.Cookies.Append(Name, token, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Path = path,
            Domain = null,
            Expires = new DateTimeOffset(DateTime.SpecifyKind(expiresAtUtc, DateTimeKind.Utc)),
            IsEssential = true,
        });
    }

    public static string? Read(HttpContext httpContext) =>
        httpContext.Request.Cookies.TryGetValue(Name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;
}
