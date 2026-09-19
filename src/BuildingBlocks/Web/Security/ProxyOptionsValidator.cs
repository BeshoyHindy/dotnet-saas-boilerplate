using System.Net;
using System.Globalization;
using Microsoft.Extensions.Options;

namespace Boilerplate.BuildingBlocks.Web.Security;

/// <summary>
/// Validates <see cref="ProxyOptions"/>. Data annotations cannot express any of this: the entries are
/// free-form strings that must parse as IP addresses / CIDR blocks, and "enabled but trusting nobody"
/// is a cross-property mistake that silently disables forwarded headers instead of failing.
/// </summary>
internal sealed class ProxyOptionsValidator : IValidateOptions<ProxyOptions>
{
    public ValidateOptionsResult Validate(string? name, ProxyOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        var failures = new List<string>();

        failures.AddRange(options.KnownProxies
            .Where(proxy => !IPAddress.TryParse(proxy, out _))
            .Select(proxy => $"ProxyOptions: KnownProxies entry '{proxy}' is not a valid IP address."));

        failures.AddRange(options.KnownNetworks
            .Where(network => !TryParseNetwork(network))
            .Select(network => $"ProxyOptions: KnownNetworks entry '{network}' is not a valid CIDR network (expected 'prefix/length')."));

        // Fail closed. Enabled with nothing trusted is not a safe middle ground: it either silently
        // ignores the proxy's headers, or — if we defaulted to trusting everyone — lets any neighbour
        // on the container network spoof the client IP. The operator has to name the proxy.
        if (!options.TrustAnyProxy && options.KnownProxies.Length == 0 && options.KnownNetworks.Length == 0)
        {
            failures.Add(
                "ProxyOptions: Enabled is true but nothing is trusted. Set ProxyOptions__KnownNetworks__0 to the proxy's " +
                "network (e.g. '10.0.0.0/8' for a Docker overlay) or ProxyOptions__KnownProxies__0 to its address. " +
                "ProxyOptions__TrustAnyProxy=true is an explicit opt-out, valid only where the app is unreachable except " +
                "through the proxy.");
        }

        return failures.Count > 0 ? ValidateOptionsResult.Fail(failures) : ValidateOptionsResult.Success;
    }

    private static bool TryParseNetwork(string value)
    {
        var separator = value.IndexOf('/', StringComparison.Ordinal);
        if (separator <= 0 || separator == value.Length - 1)
        {
            return false;
        }

        if (!IPAddress.TryParse(value.AsSpan(0, separator), out var prefix))
        {
            return false;
        }

        if (!int.TryParse(value.AsSpan(separator + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var length))
        {
            return false;
        }

        var maxLength = prefix.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? 128 : 32;
        return length >= 0 && length <= maxLength;
    }
}
