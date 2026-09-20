namespace Boilerplate.BuildingBlocks.Web.Health;

/// <summary>
/// Tags that decide which checks each health endpoint runs.
/// </summary>
/// <remarks>
/// Readiness is polled continuously by the proxy, so it must stay cheap: tag a check
/// <see cref="Ready"/> only when the API genuinely cannot serve requests without that dependency.
/// Every module's DbContext check talks to the same PostgreSQL server, so one representative check
/// (the tenant catalog) answers the readiness question that five would; the rest are reported by
/// <c>GET /health</c>, which runs everything on demand.
/// </remarks>
public static class HealthTags
{
    /// <summary>Process-level checks with no external dependency.</summary>
    public const string Live = "live";

    /// <summary>Dependencies without which the API cannot serve traffic.</summary>
    public const string Ready = "ready";
}
