using System.ComponentModel.DataAnnotations;

namespace Boilerplate.BuildingBlocks.Web.Security;

/// <summary>
/// Reverse-proxy (Traefik) configuration for <c>UseForwardedHeaders</c>. The API is published behind
/// a proxy that terminates TLS, so without this the app sees the proxy's IP and scheme http — which
/// breaks HTTPS redirect decisions, generated absolute URLs and IP-partitioned rate limiting.
/// </summary>
public sealed class ProxyOptions
{
    public const string SectionName = "ProxyOptions";

    /// <summary>
    /// Whether <c>X-Forwarded-For</c>/<c>-Proto</c>/<c>-Host</c> are honoured. Off by default: the
    /// headers are caller-supplied, so they may only be trusted where a proxy actually strips and
    /// re-writes them.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// IP addresses of the proxies allowed to set forwarded headers (e.g. the Traefik container IP).
    /// </summary>
    public string[] KnownProxies { get; set; } = [];

    /// <summary>
    /// CIDR networks whose members are allowed to set forwarded headers, as <c>prefix/length</c>
    /// (e.g. <c>10.0.0.0/8</c> for a Docker overlay network).
    /// </summary>
    public string[] KnownNetworks { get; set; } = [];

    /// <summary>
    /// Accept forwarded headers from any peer. Only safe when the container is unreachable except
    /// through the proxy (the Dokploy/Traefik shape in ADR-0005), because it lets the immediate peer
    /// spoof the client IP and scheme.
    /// </summary>
    public bool TrustAnyProxy { get; set; }

    /// <summary>Number of proxy hops to unwrap; one entry per proxy in front of the app.</summary>
    [Range(1, 16)]
    public int ForwardLimit { get; set; } = 1;
}
