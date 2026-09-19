using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;

namespace Boilerplate.BuildingBlocks.Web.Security;

/// <summary>
/// Translates <see cref="ProxyOptions"/> into ASP.NET Core's <see cref="ForwardedHeadersOptions"/>.
/// </summary>
internal sealed class ConfigureForwardedHeaders(IOptions<ProxyOptions> options) : IConfigureOptions<ForwardedHeadersOptions>
{
    public void Configure(ForwardedHeadersOptions forwarded)
    {
        ArgumentNullException.ThrowIfNull(forwarded);

        var proxy = options.Value;
        if (!proxy.Enabled)
        {
            return;
        }

        // XForwardedHost is deliberately NOT honoured. Host filtering runs before this middleware, so
        // accepting X-Forwarded-Host would let a caller rewrite Request.Host after the allow-list has
        // already approved the real one — and Request.Host is interpolated into the confirmation and
        // password-reset links the Identity module mails out. Traefik forwards the original Host
        // header, so there is nothing to recover from the forwarded one.
        forwarded.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        forwarded.ForwardLimit = proxy.ForwardLimit;

        // The defaults trust loopback only, which never matches a proxy running in another container.
        forwarded.KnownProxies.Clear();
        forwarded.KnownIPNetworks.Clear();

        if (proxy.TrustAnyProxy)
        {
            // Empty allow-lists mean "accept from any peer" — deliberate, and only valid where the
            // proxy is the sole route to the app.
            return;
        }

        // Parse defensively: ProxyOptionsValidator already rejects malformed entries with a message
        // that names the offending value, and a raw FormatException from here would bury it.
        foreach (var address in proxy.KnownProxies)
        {
            if (IPAddress.TryParse(address, out var parsed))
            {
                forwarded.KnownProxies.Add(parsed);
            }
        }

        foreach (var network in proxy.KnownNetworks)
        {
            var separator = network.IndexOf('/', StringComparison.Ordinal);
            if (separator <= 0 || separator == network.Length - 1)
            {
                continue;
            }

            if (IPAddress.TryParse(network.AsSpan(0, separator), out var prefix) &&
                int.TryParse(network.AsSpan(separator + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var length))
            {
                forwarded.KnownIPNetworks.Add(new(prefix, length));
            }
        }
    }
}
