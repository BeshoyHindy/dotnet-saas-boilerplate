namespace Boilerplate.Modules.Multitenancy;

/// <summary>
/// Tenant-lifecycle validity knobs (config section <c>"TenantValidity"</c>): how long a tenant is
/// valid for when created or renewed without an explicit term, and how long past <c>ValidUpto</c> a
/// tenant keeps working before being hard-blocked.
/// </summary>
public sealed class TenantValidityOptions
{
    public const string SectionName = "TenantValidity";

    /// <summary>Months of validity granted when CreateTenant/RenewTenant is called without an explicit term.</summary>
    public int DefaultValidityMonths { get; set; } = 1;

    /// <summary>Days past <c>ValidUpto</c> during which requests/logins still succeed.</summary>
    public int GracePeriodDays { get; set; } = 7;

    /// <summary>How many days before <c>ValidUpto</c> the daily scan starts sending "nearing expiry" reminders.</summary>
    public int ExpiryNotificationLeadDays { get; set; } = 7;
}
