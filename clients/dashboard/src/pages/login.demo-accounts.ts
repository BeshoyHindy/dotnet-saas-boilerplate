/**
 * The demo accounts the sign-in page can offer, when demo mode is on.
 *
 * Static, not fetched: the login page is anonymous, and an endpoint that listed real
 * accounts — even "demo" ones — would be user and tenant enumeration (ADR-0002). This is a
 * hand-curated mirror of what the server's demo seeder creates; if the seeder changes, change
 * this list too.
 *
 * **No password lives here.** The shared demo password is runtime configuration
 * (`APP_DEMO_PASSWORD` / `VITE_DEMO_PASSWORD`, read through `env.demoPassword`), never a
 * literal in the repository — a committed credential is a committed credential whatever we
 * call the environment it belongs to. With demo mode on and no password configured, the
 * picker fills the tenant and email and lets the person type it.
 */

export type DemoTier = "tenant-admin" | "manager" | "basic";

export type DemoAccount = {
  email: string;
  tenant: string;
  tenantLabel: string;
  firstName: string;
  lastName: string;
  /** Pre-baked role for the chip in the row. */
  tier: DemoTier;
  /** One-line persona explainer shown under the name. */
  persona: string;
};

export type DemoAccountGroup = {
  tenant: string;
  tenantLabel: string;
  blurb: string;
  accounts: DemoAccount[];
};

const account = (
  tenant: string,
  tenantLabel: string,
  email: string,
  firstName: string,
  lastName: string,
  tier: DemoTier,
  persona: string,
): DemoAccount => ({ email, tenant, tenantLabel, firstName, lastName, tier, persona });

/**
 * Grouped by tenant, in the order the rail shows them: Acme first because it is the populated
 * demo where most flows make sense, then Globex.
 *
 * Identity-only personas — a tenant administrator, a manager on a custom role, and plain
 * members. The catalog/ticket personas the old list carried belonged to modules this template
 * does not ship.
 */
export const DEMO_ACCOUNT_GROUPS: DemoAccountGroup[] = [
  {
    tenant: "acme",
    tenantLabel: "Acme Corp",
    blurb: "populated · most flows make sense here",
    accounts: [
      account("acme", "Acme Corp", "admin@acme.com", "Acme", "Admin", "tenant-admin", "Tenant administrator — full access"),
      account("acme", "Acme Corp", "manager@acme.com", "Maya", "Lin", "manager", "Custom role — manages users, not roles"),
      account("acme", "Acme Corp", "alice@acme.com", "Alice", "Nguyen", "basic", "Default member"),
      account("acme", "Acme Corp", "bob@acme.com", "Bob", "Patel", "basic", "Default member"),
    ],
  },
  {
    tenant: "globex",
    tenantLabel: "Globex",
    blurb: "onboarding · sparse data",
    accounts: [
      account("globex", "Globex", "admin@globex.com", "Globex", "Admin", "tenant-admin", "Tenant administrator — full access"),
      account("globex", "Globex", "dave@globex.com", "Dave", "Hartwell", "basic", "Default member"),
    ],
  },
];

export const TIER_LABEL: Record<DemoTier, string> = {
  "tenant-admin": "Tenant Admin",
  manager: "Manager",
  basic: "Basic",
};

/**
 * What picking a demo account should do, given the runtime config.
 *
 * A pure function because the interesting case is the awkward one: demo mode on, no
 * password configured. Signing in then means firing a request that can only 401, so the
 * picker fills in what it knows and hands over the password field instead. Keeping that
 * decision out of the component makes it a unit test rather than a rendered flow.
 */
export type DemoPickOutcome =
  | { action: "sign-in"; email: string; tenant: string; password: string }
  | { action: "prefill"; email: string; tenant: string; reason: string };

export function demoPickOutcome(account: DemoAccount, demoPassword: string): DemoPickOutcome {
  if (!demoPassword) {
    return {
      action: "prefill",
      email: account.email,
      tenant: account.tenant,
      reason: `Demo mode is on but no demo password is configured — enter the password for ${account.email}.`,
    };
  }
  return {
    action: "sign-in",
    email: account.email,
    tenant: account.tenant,
    password: demoPassword,
  };
}
