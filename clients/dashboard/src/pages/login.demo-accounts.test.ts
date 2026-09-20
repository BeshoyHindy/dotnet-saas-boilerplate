import { describe, expect, it } from "vitest";
import {
  DEMO_ACCOUNT_GROUPS,
  demoPickOutcome,
  TIER_LABEL,
  type DemoAccount,
} from "@/pages/login.demo-accounts";

/**
 * The picker's contract, and the one thing that must never regress: no password is
 * written down here. The shared one is runtime config (`env.demoPassword`), and the
 * login page's fallback when it is absent is asserted in `login.demo-fallback.test.ts`.
 */
describe("demo accounts", () => {
  const accounts = DEMO_ACCOUNT_GROUPS.flatMap((g) => g.accounts);

  it("carries no credential — only who the account is", () => {
    for (const account of accounts) {
      expect(Object.keys(account)).not.toContain("password");
      expect(JSON.stringify(account).toLowerCase()).not.toContain("password");
    }
  });

  it("gives every account the tenant it belongs to, so nobody picks one", () => {
    // Picking a demo account must be a complete answer: tenant AND email.
    for (const group of DEMO_ACCOUNT_GROUPS) {
      for (const account of group.accounts) {
        expect(account.tenant).toBe(group.tenant);
        expect(account.tenant).toMatch(/^[a-z0-9][a-z0-9-]*$/);
        expect(account.email).toContain("@");
      }
    }
  });

  it("labels every tier it uses", () => {
    for (const account of accounts) {
      expect(TIER_LABEL[account.tier]).toBeTruthy();
    }
  });

  it("lists the tenants the demo seeder creates, and no real customer", () => {
    expect(DEMO_ACCOUNT_GROUPS.map((g) => g.tenant)).toEqual(["acme", "globex"]);
  });
});

describe("demoPickOutcome", () => {
  const account: DemoAccount = {
    email: "admin@acme.com",
    tenant: "acme",
    tenantLabel: "Acme Corp",
    firstName: "Acme",
    lastName: "Admin",
    tier: "tenant-admin",
    persona: "Tenant administrator — full access",
  };

  it("signs in with the account's own tenant when a password is configured", () => {
    expect(demoPickOutcome(account, "s3cret")).toEqual({
      action: "sign-in",
      email: "admin@acme.com",
      tenant: "acme",
      password: "s3cret",
    });
  });

  it("prefills instead of signing in when no password is configured", () => {
    // Demo mode on, APP_DEMO_PASSWORD unset: a sign-in could only 401, so fill in what
    // we know and let the person type the rest.
    const outcome = demoPickOutcome(account, "");
    expect(outcome.action).toBe("prefill");
    expect(outcome).toMatchObject({ email: "admin@acme.com", tenant: "acme" });
    expect(outcome.action === "prefill" && outcome.reason).toContain("admin@acme.com");
  });

  it("never invents a password", () => {
    // The prose mentions the word; what must not exist is a password VALUE to send.
    const outcome = demoPickOutcome(account, "");
    expect(Object.keys(outcome)).not.toContain("password");
  });
});
