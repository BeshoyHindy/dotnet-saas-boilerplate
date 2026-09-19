using System.ComponentModel.DataAnnotations;

namespace Boilerplate.BuildingBlocks.Web.Limits;

/// <summary>
/// Kestrel request limits. Uploads go straight to object storage through presigned URLs, so the API
/// itself never needs to accept large bodies; capping them keeps a single request from pinning
/// memory and buying an attacker a cheap denial of service.
/// </summary>
public sealed class RequestLimitsOptions
{
    public const string SectionName = "RequestLimits";

    /// <summary>Maximum request body, in bytes (default 10 MiB).</summary>
    [Range(1024, 1073741824)]
    public long MaxRequestBodyBytes { get; set; } = 10 * 1024 * 1024;

    /// <summary>Maximum size of all request headers combined, in bytes (default 32 KiB).</summary>
    [Range(1024, 1048576)]
    public int MaxRequestHeadersTotalBytes { get; set; } = 32 * 1024;

    /// <summary>Maximum request line (method + URI + version), in bytes (default 8 KiB).</summary>
    [Range(1024, 65536)]
    public int MaxRequestLineBytes { get; set; } = 8 * 1024;
}
