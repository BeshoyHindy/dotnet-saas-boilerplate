using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Options;

namespace Boilerplate.BuildingBlocks.Web.Limits;

/// <summary>
/// Applies <see cref="RequestLimitsOptions"/> to Kestrel. Registered after Kestrel's own
/// configuration binding, so these values win over the framework defaults (30 MB body, 32 KB
/// headers) while an operator can still raise them per environment.
/// </summary>
internal sealed class ConfigureRequestLimits(IOptions<RequestLimitsOptions> options) : IConfigureOptions<KestrelServerOptions>
{
    public void Configure(KestrelServerOptions kestrel)
    {
        ArgumentNullException.ThrowIfNull(kestrel);

        var limits = options.Value;
        kestrel.Limits.MaxRequestBodySize = limits.MaxRequestBodyBytes;
        kestrel.Limits.MaxRequestHeadersTotalSize = limits.MaxRequestHeadersTotalBytes;
        kestrel.Limits.MaxRequestLineSize = limits.MaxRequestLineBytes;
    }
}
