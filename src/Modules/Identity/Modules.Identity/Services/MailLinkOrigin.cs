using Boilerplate.BuildingBlocks.Web.Origin;
using Microsoft.Extensions.Options;

namespace Boilerplate.Modules.Identity.Services;

/// <summary>
/// The base URL every emailed link is built on. It comes from configuration (<c>OriginOptions</c>)
/// and never from the request: <c>Request.Host</c> is attacker-influenceable behind a proxy, and a
/// link in an email is exactly the place where a swapped host becomes an account-takeover primitive.
/// Password-reset, registration and resend-confirmation all resolve it through here so they cannot
/// drift apart.
/// </summary>
internal static class MailLinkOrigin
{
    /// <exception cref="InvalidOperationException">No origin is configured.</exception>
    public static string Require(IOptions<OriginOptions> originOptions)
    {
        var origin = originOptions?.Value?.OriginUrl?.ToString();

        return string.IsNullOrWhiteSpace(origin)
            ? throw new InvalidOperationException("Origin URL is not configured.")
            : origin;
    }
}
