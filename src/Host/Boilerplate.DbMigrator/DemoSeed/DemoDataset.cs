using System.Collections.ObjectModel;
using Boilerplate.BuildingBlocks.Shared.Constants;
using Boilerplate.Modules.Auditing.Contracts.Authorization;
using Boilerplate.Modules.Files.Contracts.Authorization;
using Boilerplate.Modules.Identity.Contracts.Authorization;
using Boilerplate.Modules.Multitenancy.Contracts.Authorization;
using Boilerplate.Modules.Notifications.Contracts.Authorization;

namespace Boilerplate.DbMigrator.DemoSeed;

/// <summary>
/// The demo content itself — kept separate from <see cref="DemoSeeder"/>, which owns the how.
///
/// This is the one place a product built from this template edits (or deletes): the tenants,
/// people, roles and groups a fresh stack comes up with so there is something to sign in as.
/// Nothing here is structural — the framework's own seed (root tenant, default roles, system
/// groups, tenant admin) is <c>IdentityDbInitializer</c>'s job and runs with or without
/// <c>--demo</c>.
/// </summary>
internal static class DemoDataset
{
    /// <summary>Acme Corp — the populated tenant, where most flows have something to look at.</summary>
    public static DemoTenant Acme { get; } = new(
        Id: "acme",
        Name: "Acme Corp",
        AdminEmail: "admin@acme.com",
        Issuer: "acme.example.com",
        Users: new ReadOnlyCollection<DemoUser>(
        [
            new("acme.manager", "manager@acme.com", "Maya",  "Lin",    DemoRoles.ManagerName),
            new("acme.support", "support@acme.com", "Sam",   "Rivera", DemoRoles.SupportName),
            new("acme.alice",   "alice@acme.com",   "Alice", "Nguyen", RoleConstants.Basic),
            new("acme.bob",     "bob@acme.com",     "Bob",   "Patel",  RoleConstants.Basic),
        ]),
        Groups: new ReadOnlyCollection<DemoGroup>(
        [
            new("Engineering",
                "Product engineering — the people who ship the thing.",
                new ReadOnlyCollection<string>(["alice@acme.com", "bob@acme.com"])),
            new("Support Desk",
                "Front line: customer conversations and escalations.",
                new ReadOnlyCollection<string>(["manager@acme.com", "support@acme.com"])),
        ]));

    /// <summary>Globex — the sparse tenant, useful for "what does a new customer see".</summary>
    public static DemoTenant Globex { get; } = new(
        Id: "globex",
        Name: "Globex",
        AdminEmail: "admin@globex.com",
        Issuer: "globex.example.com",
        Users: new ReadOnlyCollection<DemoUser>(
        [
            new("globex.dave", "dave@globex.com", "Dave", "Hartwell", RoleConstants.Basic),
        ]),
        Groups: new ReadOnlyCollection<DemoGroup>(
        [
            new("Operations",
                "Day-to-day operations for the Globex account.",
                new ReadOnlyCollection<string>(["admin@globex.com", "dave@globex.com"])),
        ]));

    /// <summary>Every demo tenant, in the order they are seeded.</summary>
    public static IReadOnlyList<DemoTenant> Tenants { get; } =
        new ReadOnlyCollection<DemoTenant>([Acme, Globex]);

    /// <summary>
    /// Custom roles seeded into every demo tenant, on top of the framework's Admin and Basic.
    /// They exist to show what a role that is neither of those looks like — a named bundle of
    /// permissions, nothing more.
    ///
    /// Permissions are referenced through the module contract constants, never hand-typed: a
    /// name that does not match a registry entry is a claim that grants nothing, silently.
    /// </summary>
    internal static class DemoRoles
    {
        public const string ManagerName = "Manager";
        public const string SupportName = "Support";

        /// <summary>Operations manager — runs the tenant's people, keeps out of platform settings.</summary>
        public static DemoRole Manager { get; } = new(
            ManagerName,
            "Operations manager — manages users, groups, sessions and the tenant's branding.",
            new ReadOnlyCollection<string>(
            [
                IdentityPermissions.Users.View,
                IdentityPermissions.Users.Search,
                IdentityPermissions.Users.Update,
                IdentityPermissions.Users.ManageRoles,
                IdentityPermissions.UserRoles.View,
                IdentityPermissions.UserRoles.Update,
                IdentityPermissions.Roles.View,
                IdentityPermissions.RoleClaims.View,
                IdentityPermissions.Groups.View,
                IdentityPermissions.Groups.Update,
                IdentityPermissions.Groups.ManageMembers,
                IdentityPermissions.Sessions.View,
                IdentityPermissions.Sessions.Revoke,
                IdentityPermissions.Sessions.ViewAll,
                IdentityPermissions.Sessions.RevokeAll,
                AuditingPermissions.AuditTrails.View,
                FilesPermissions.Upload,
                FilesPermissions.DeleteOwn,
                FilesPermissions.ViewTrash,
                FilesPermissions.Restore,
                NotificationPermissions.Inbox.View,
                NotificationPermissions.Inbox.MarkRead,
                MultitenancyPermissions.Tenants.ViewTheme,
                MultitenancyPermissions.Tenants.UpdateTheme,
            ]));

        /// <summary>Support agent — reads people and sessions, changes almost nothing.</summary>
        public static DemoRole Support { get; } = new(
            SupportName,
            "Support agent — read-only on people, may revoke a stuck session.",
            new ReadOnlyCollection<string>(
            [
                IdentityPermissions.Users.View,
                IdentityPermissions.Users.Search,
                IdentityPermissions.UserRoles.View,
                IdentityPermissions.Groups.View,
                IdentityPermissions.Sessions.View,
                IdentityPermissions.Sessions.Revoke,
                IdentityPermissions.Sessions.ViewAll,
                IdentityPermissions.Sessions.RevokeAll,
                AuditingPermissions.AuditTrails.View,
                FilesPermissions.Upload,
                FilesPermissions.DeleteOwn,
                NotificationPermissions.Inbox.View,
                NotificationPermissions.Inbox.MarkRead,
            ]));

        /// <summary>Both custom roles, seeded into every demo tenant.</summary>
        public static IReadOnlyList<DemoRole> All { get; } =
            new ReadOnlyCollection<DemoRole>([Manager, Support]);
    }
}

/// <summary>One demo tenant and everything seeded inside it.</summary>
/// <param name="Id">Tenant id — must satisfy the CreateTenant validator's <c>^[a-z0-9][a-z0-9-]{1,62}$</c>.</param>
/// <param name="Name">Display name.</param>
/// <param name="AdminEmail">The tenant admin, created by <c>IdentityDbInitializer</c>, not by this seeder.</param>
/// <param name="Issuer">External issuer recorded on the tenant record.</param>
/// <param name="Users">Demo users besides the tenant admin.</param>
/// <param name="Groups">Demo groups and their members (by email).</param>
internal sealed record DemoTenant(
    string Id,
    string Name,
    string AdminEmail,
    string Issuer,
    IReadOnlyList<DemoUser> Users,
    IReadOnlyList<DemoGroup> Groups);

/// <summary>One demo user. Every demo user signs in with the single configured demo password.</summary>
internal sealed record DemoUser(
    string UserName,
    string Email,
    string FirstName,
    string LastName,
    string Role);

/// <summary>A custom role and the permissions it bundles.</summary>
internal sealed record DemoRole(string Name, string Description, IReadOnlyList<string> Permissions);

/// <summary>A demo group and its members, named by email.</summary>
internal sealed record DemoGroup(string Name, string Description, IReadOnlyList<string> MemberEmails);
