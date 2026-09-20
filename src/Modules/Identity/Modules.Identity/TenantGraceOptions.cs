namespace Boilerplate.Modules.Identity;

/// <summary>
/// Login-side view of the tenant validity grace period (config section <c>"TenantValidity"</c>). A
/// tenant whose validity has lapsed can still authenticate until <c>ValidUpto + GracePeriodDays</c>.
/// </summary>
public sealed class TenantGraceOptions
{
    public const string SectionName = "TenantValidity";

    public int GracePeriodDays { get; set; } = 7;
}
