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

        if (!options.TrustAnyProxy && options.KnownProxies.Length == 0 && options.KnownNetworks.Length == 0)
        {
            failures.Add(
                "ProxyOptions: Enabled is true but no KnownProxies/KnownNetworks are configured and TrustAnyProxy is false, " +
                "so forwarded headers would be ignored. Configure the proxy addresses or set TrustAnyProxy when the app is " +
                "only reachable through the proxy.");
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
