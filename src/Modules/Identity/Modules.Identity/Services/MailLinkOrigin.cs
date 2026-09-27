using Boilerplate.BuildingBlocks.Shared.Multitenancy;
using Boilerplate.BuildingBlocks.Web.Origin;
using Finbuckle.MultiTenant.Abstractions;
using Microsoft.Extensions.Options;

namespace Boilerplate.Modules.Identity.Services;

/// <summary>
/// The base URL every emailed link is built on. It comes from configuration and never from the
/// request: <c>Request.Host</c> is attacker-influenceable behind a proxy, and a link in an email is
/// exactly the place where a swapped host becomes an account-takeover primitive. Password-reset,
/// registration and resend-confirmation all resolve it through here so they cannot drift apart.
///
/// There are two configured origins, and exactly one is picked per mail:
/// <list type="bullet">
/// <item><description><c>OriginOptions.OriginUrl</c> — the dashboard's origin (ADR-0008), the
/// default for every tenant's own users.</description></item>
/// <item><description><c>MailLinkOriginOptions.ConsoleOriginUrl</c> — the console's origin, used
/// only when the mail's recipient belongs to the root tenant (an operator), and only when it is
/// configured at all. A <c>--frontend false</c> scaffold never sets it, which is exactly the case
/// <see cref="Require"/> falls back on.</description></item>
/// </list>
/// </summary>
internal static class MailLinkOrigin
{
    /// <exception cref="InvalidOperationException">No origin is configured.</exception>
    public static string Require(
        IOptions<OriginOptions> originOptions,
        IOptions<MailLinkOriginOptions> mailLinkOriginOptions,
        IMultiTenantContextAccessor<AppTenantInfo> multiTenantContextAccessor)
    {
        var origin = originOptions?.Value?.OriginUrl?.ToString();

        if (string.IsNullOrWhiteSpace(origin))
        {
            throw new InvalidOperationException("Origin URL is not configured.");
        }

        var isRootTenant = string.Equals(
            multiTenantContextAccessor?.MultiTenantContext?.TenantInfo?.Id,
            MultitenancyConstants.Root.Id,
            StringComparison.Ordinal);

        var consoleOrigin = mailLinkOriginOptions?.Value?.ConsoleOriginUrl?.ToString();

        return isRootTenant && !string.IsNullOrWhiteSpace(consoleOrigin) ? consoleOrigin : origin;
    }
}
