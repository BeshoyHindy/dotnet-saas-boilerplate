namespace Boilerplate.BuildingBlocks.Shared.Constants;

/// <summary>
/// Cross-cutting platform permissions that don't belong to a specific business module.
/// Registered automatically during <c>AddHeroPlatform</c>.
/// </summary>
public static class SystemPermissions
{
    public static class Hangfire
    {
        public const string Resource = nameof(Hangfire);
        public const string View = $"Permissions.{Resource}.View";
    }

    public static class Dashboard
    {
        public const string Resource = nameof(Dashboard);
        public const string View = $"Permissions.{Resource}.View";
    }

    /// <summary>
    /// Platform-scoped permissions held only by the SuperAdmin (root tenant Admin role).
    /// Tagged <c>IsRoot=true</c>, which means <see cref="IPermissionRegistry.Admin"/> filters
    /// them out for non-root tenants and <see cref="IPermissionRegistry.Root"/> picks them up
    /// for the root tenant. Use these for cross-tenant operations: managing tenants, system-wide
    /// audits, and the platform impersonation pathway.
    /// </summary>
    public static class Platform
    {
        public const string Tenants = $"{nameof(Platform)}.Tenants";
        public const string Audits = $"{nameof(Platform)}.Audits";
        public const string Users = $"{nameof(Platform)}.Users";
    }

    public static IReadOnlyList<AppPermission> All { get; } =
    [
        // Operator-only: the Hangfire dashboard exposes every tenant's jobs and lets the viewer
        // requeue or delete them, so it belongs to the platform operator, not to tenant members.
        new("View Hangfire",  ActionConstants.View, Hangfire.Resource,  IsRoot: true),
        new("View Dashboard", ActionConstants.View, Dashboard.Resource, IsBasic: true),

        // Platform · cross-tenant — SuperAdmin only.
        new("View All Tenants",          ActionConstants.View,   Platform.Tenants,       IsRoot: true),
        new("Create Tenants",            ActionConstants.Create, Platform.Tenants,       IsRoot: true),
        new("Update Tenants",            ActionConstants.Update, Platform.Tenants,       IsRoot: true),
        new("Suspend Tenants",           "Suspend",              Platform.Tenants,       IsRoot: true),
        new("Delete Tenants",            ActionConstants.Delete, Platform.Tenants,       IsRoot: true),

        new("View Cross-Tenant Audits",  "ViewAll",              Platform.Audits,        IsRoot: true),
        new("Cross-Tenant Impersonate",  "Impersonate",          Platform.Users,         IsRoot: true),
    ];
}
