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

        forwarded.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
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

        foreach (var address in proxy.KnownProxies)
        {
            forwarded.KnownProxies.Add(IPAddress.Parse(address));
        }

        foreach (var network in proxy.KnownNetworks)
        {
            var separator = network.IndexOf('/', StringComparison.Ordinal);
            var prefix = IPAddress.Parse(network.AsSpan(0, separator));
            var length = int.Parse(network.AsSpan(separator + 1), System.Globalization.CultureInfo.InvariantCulture);
            forwarded.KnownIPNetworks.Add(new(prefix, length));
        }
    }
}
