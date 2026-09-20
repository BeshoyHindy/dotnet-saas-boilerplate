import {
  Building2,
  HeartPulse,
  LayoutDashboard,
  Palette,
  Plus,
  ScrollText,
  Settings as SettingsIcon,
  Shield,
  ShieldCheck,
  UserCog,
  Users,
  UserRound,
} from "lucide-react";
import { IdentityPermissions, MultitenancyPermissions } from "@/lib/permissions";

/**
 * A palette entry whose whole job is to navigate: the target as data, not a closure.
 * Same permission semantics as NavSpec in layout/nav-data.ts — the entry is hidden
 * unless the user holds `perm` AND at least one of `anyPerm`, mirroring what the
 * destination's API enforces, so the palette never offers a guaranteed 403.
 */
export type NavigationAction = {
  id: string;
  label: string;
  hint?: string;
  Icon: React.ComponentType<{ className?: string }>;
  /** Free-form keywords for fuzzy matching. */
  keywords?: string[];
  perm?: string;
  anyPerm?: readonly string[];
  /** Route this entry sends the user to. */
  to: string;
};

export type NavigationGroup = { heading: string; items: NavigationAction[] };

/**
 * Every palette entry that navigates, declared at module scope so the targets can be
 * checked. `nav-targets.test.ts` asserts each `to` resolves to a route registered in
 * `routes.tsx` — two entries here (`/settings/api-keys`, `/settings/notifications`)
 * pointed at pages that only ever existed in the retired two-app UI and 404'd.
 */
export const navigationGroups: NavigationGroup[] = [
  {
    heading: "Navigate",
    items: [
      {
        id: "nav-overview",
        label: "Overview",
        hint: "Platform snapshot",
        Icon: LayoutDashboard,
        keywords: ["home", "dashboard"],
        to: "/",
      },
      {
        id: "nav-tenants",
        label: "Tenants",
        hint: "The tenant registry — enter, renew, deactivate",
        Icon: Building2,
        keywords: ["customers", "organisations", "registry", "operator"],
        to: "/tenants",
        perm: MultitenancyPermissions.Tenants.View,
      },
      {
        id: "nav-impersonation",
        label: "Impersonation",
        hint: "Active acting grants",
        Icon: UserCog,
        keywords: ["acting", "grants", "operator", "support"],
        to: "/impersonation",
        perm: IdentityPermissions.Impersonation.View,
      },
      {
        id: "nav-users",
        label: "Users",
        hint: "Identity directory",
        Icon: Users,
        keywords: ["identity", "people", "members", "team"],
        to: "/identity/users",
        perm: "Permissions.Users.Update",
      },
      {
        id: "nav-roles",
        label: "Roles",
        hint: "Permissions & role assignment",
        Icon: ShieldCheck,
        keywords: ["identity", "permissions", "rbac"],
        to: "/identity/roles",
        perm: "Permissions.Roles.Update",
      },
      {
        id: "nav-groups",
        label: "Groups",
        hint: "Org groups & membership",
        Icon: Users,
        keywords: ["identity", "teams", "org"],
        to: "/identity/groups",
        perm: "Permissions.Groups.Update",
      },
      {
        id: "nav-health",
        label: "Health",
        hint: "Readiness probe & dependencies",
        Icon: HeartPulse,
        keywords: ["status", "uptime", "system", "ready", "redis", "postgres"],
        to: "/system/health",
      },
      {
        id: "nav-audits",
        label: "Audit trail",
        hint: "Activity, security, entity-change events",
        Icon: ScrollText,
        keywords: ["audit", "log", "compliance", "security", "trace", "correlation"],
        to: "/system/audits",
        perm: "Permissions.AuditTrails.View",
      },
      {
        id: "nav-sessions",
        label: "Sessions",
        hint: "Active user sessions",
        Icon: Shield,
        keywords: ["devices", "logins"],
        to: "/system/sessions",
        perm: "Permissions.Sessions.ViewAll",
      },
      {
        id: "nav-settings",
        label: "Settings",
        Icon: SettingsIcon,
        keywords: ["preferences", "config"],
        to: "/settings",
      },
    ],
  },
  {
    heading: "Create",
    items: [
      {
        id: "create-user",
        label: "Create user",
        hint: "Register a new account",
        Icon: Plus,
        keywords: ["new", "invite", "register", "identity"],
        to: "/identity/users?action=create",
        perm: "Permissions.Users.Create",
      },
      {
        id: "create-role",
        label: "Create role",
        hint: "Define a new permission set",
        Icon: Plus,
        keywords: ["new", "permissions", "rbac"],
        to: "/identity/roles?action=create",
        perm: "Permissions.Roles.Create",
      },
      {
        id: "create-group",
        label: "Create group",
        hint: "Organize members",
        Icon: Plus,
        keywords: ["new", "team", "org"],
        to: "/identity/groups?action=create",
        perm: "Permissions.Groups.Create",
      },
      {
        id: "create-tenant",
        label: "Create tenant",
        hint: "Provision a new customer",
        Icon: Plus,
        keywords: ["new", "customer", "organisation", "provision"],
        to: "/tenants?action=create",
        perm: MultitenancyPermissions.Tenants.Create,
      },
    ],
  },
  {
    heading: "Account",
    items: [
      {
        id: "acc-profile",
        label: "Profile",
        hint: "Name, email, contact",
        Icon: UserRound,
        to: "/settings/profile",
      },
      {
        id: "acc-security",
        label: "Security",
        hint: "Password, 2FA, sessions",
        Icon: Shield,
        keywords: ["password", "2fa", "sessions"],
        to: "/settings/security",
      },
      {
        id: "acc-appearance",
        label: "Appearance",
        hint: "Theme, accent, font, density",
        Icon: Palette,
        keywords: ["theme", "font", "density", "dark", "light"],
        to: "/settings/appearance",
      },
    ],
  },
];
