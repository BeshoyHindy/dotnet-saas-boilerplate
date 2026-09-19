/**
 * Permission strings + catalog mirrored from the server's *Permissions.cs
 * registries. Kept here so:
 *   1. Route guards stay typo-proof (`IdentityPermissions.Users.View`).
 *
 * Only the ones routes gate on live here; the Role editor gets the full set from the
 * server's catalog endpoint. Convention follows the server:
 * `Permissions.{Resource}.{Action}`.
 */

export const IdentityPermissions = Object.freeze({
  Users: {
    View: "Permissions.Users.View",
    Search: "Permissions.Users.Search",
    Create: "Permissions.Users.Create",
    Update: "Permissions.Users.Update",
    Delete: "Permissions.Users.Delete",
    Export: "Permissions.Users.Export",
    ManageRoles: "Permissions.Users.ManageRoles",
    Impersonate: "Permissions.Users.Impersonate",
  },
  UserRoles: {
    View: "Permissions.UserRoles.View",
    Update: "Permissions.UserRoles.Update",
  },
  Roles: {
    View: "Permissions.Roles.View",
    Create: "Permissions.Roles.Create",
    Update: "Permissions.Roles.Update",
    Delete: "Permissions.Roles.Delete",
  },
  RoleClaims: {
    View: "Permissions.RoleClaims.View",
    Update: "Permissions.RoleClaims.Update",
  },
  Sessions: {
    View: "Permissions.Sessions.View",
    Revoke: "Permissions.Sessions.Revoke",
    ViewAll: "Permissions.Sessions.ViewAll",
    RevokeAll: "Permissions.Sessions.RevokeAll",
  },
  Impersonation: {
    View: "Permissions.Impersonation.View",
    Revoke: "Permissions.Impersonation.Revoke",
  },
} as const);

export const MultitenancyPermissions = Object.freeze({
  Tenants: {
    View: "Permissions.Tenants.View",
    Create: "Permissions.Tenants.Create",
    Update: "Permissions.Tenants.Update",
    UpgradeSubscription: "Permissions.Tenants.UpgradeSubscription",
  },
} as const);

/**
 * Platform-scoped permissions — held only by the root tenant's Admin role (`IsRoot` in the
 * server catalog, `SystemPermissions.Platform` server-side). `CrossTenantImpersonate` gates
 * the operator token exchange: entering another tenant with a short-lived, audited token
 * (ADR-0002). It is the catalog's long-standing "Cross-Tenant Impersonate" entry — the
 * exchange *is* that capability, so there is one name for it and no role claim to migrate.
 */
export const SystemPermissions = Object.freeze({
  Platform: {
    CrossTenantImpersonate: "Permissions.Platform.Users.Impersonate",
  },
} as const);

export const AuditingPermissions = Object.freeze({
  AuditTrails: {
    View: "Permissions.AuditTrails.View",
    ViewCrossTenant: "Permissions.AuditTrails.ViewCrossTenant",
  },
} as const);

// No catalog here. The role editor reads GET /api/v1/identity/permissions/catalog
// (`getPermissionsCatalog`), so every module's permissions reach the editor without a
// static mirror. These constants exist only so route guards stay typo-proof; add one
// when a *route* has to name a permission the server enforces.
