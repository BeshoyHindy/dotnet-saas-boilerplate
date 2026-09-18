using Boilerplate.BuildingBlocks.Shared.Quota;

namespace Boilerplate.BuildingBlocks.Quota;

/// <summary>
/// Outcome of a quota check. <see cref="Allowed"/> is false when the requested amount would
/// push current usage past <see cref="Limit"/>. <see cref="ResetAtUtc"/> indicates when the
/// counter resets (null for gauge-based resources that have no period boundary).
/// </summary>
public sealed record QuotaCheckResult(
    bool Allowed,
    QuotaResource Resource,
    long CurrentUsage,
    long Limit,
    DateTimeOffset? ResetAtUtc)
{
    public static QuotaCheckResult Unlimited(QuotaResource resource, long currentUsage)
        => new(true, resource, currentUsage, long.MaxValue, null);
}
