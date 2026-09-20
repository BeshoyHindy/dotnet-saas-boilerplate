import { expect, test } from "@playwright/test";
import { mockJsonResponse } from "../helpers/api-mocks";
import { seedAuthedSession } from "../helpers/auth-seed";
import { installShellMocks, OPERATOR_PERMISSIONS, paged } from "../helpers/shell-mocks";

/**
 * Operator enters a tenant — the console's smoke journey (ADR-0008).
 *
 * This is what the console exists for, so it exercises the whole seam:
 * the permission-gated /tenants route resolves for an operator, the tenant page offers
 * "Enter tenant", and confirming exchanges the operator's token for a short-lived one
 * that names the target tenant (ADR-0002, issue #9).
 *
 * The exchanged token is held in memory only — never localStorage — so the assertions
 * are about what the UI does with it (the acting banner, the exit), not about storage.
 */

const OPERATOR = {
  sub: "op-1",
  email: "root@boilerplate.local",
  firstName: "Root",
  lastName: "Operator",
  tenant: "root",
};

const TENANT = {
  id: "acme",
  name: "Acme Corp",
  adminEmail: "admin@acme.com",
  isActive: true,
  validUpto: new Date(Date.now() + 365 * 24 * 60 * 60 * 1000).toISOString(),
  issuer: null,
  expiryState: "Active",
  graceEndsUtc: new Date(Date.now() + 372 * 24 * 60 * 60 * 1000).toISOString(),
};

const TARGET_ADMIN = {
  id: "00000000-0000-0000-0000-0000000000a0",
  name: "Acme Admin",
};

/** A JWT-shaped acting token: `act_sub` is the operator, `tenant` the target. */
function actingToken(): string {
  const b64url = (obj: unknown) =>
    btoa(JSON.stringify(obj)).replace(/=+$/, "").replace(/\+/g, "-").replace(/\//g, "_");
  const payload = {
    sub: TARGET_ADMIN.id,
    email: TENANT.adminEmail,
    name: TARGET_ADMIN.name,
    tenant: TENANT.id,
    jti: "grant-jti-1",
    act_sub: OPERATOR.sub,
    act_tenant: OPERATOR.tenant,
    act_name: "Root Operator",
    exp: Math.floor(Date.now() / 1000) + 900,
    iat: Math.floor(Date.now() / 1000),
  };
  return [b64url({ alg: "HS256", typ: "JWT" }), b64url(payload), "sig"].join(".");
}

const EXCHANGE_RESPONSE = {
  accessToken: actingToken(),
  accessTokenExpiresAt: new Date(Date.now() + 900_000).toISOString(),
  targetTenantId: TENANT.id,
  targetUserId: TARGET_ADMIN.id,
  targetUserName: TARGET_ADMIN.name,
  actorUserId: OPERATOR.sub,
  actorTenantId: OPERATOR.tenant,
  grantId: "11111111-1111-1111-1111-111111111111",
  jti: "grant-jti-1",
};

test.describe("operator enters a tenant", () => {
  test.beforeEach(async ({ page }) => {
    await seedAuthedSession(page, OPERATOR);
    await installShellMocks(page);
    // Spelled out here too: this spec is about what the operator grant unlocks.
    await mockJsonResponse(page, "**/api/v1/identity/permissions", [...OPERATOR_PERMISSIONS]);
    // A RegExp, not a glob: the query string is what distinguishes the registry
    // listing from the per-tenant routes mocked below.
    await page.route(/\/api\/v1\/tenants\?/, (route) =>
      route.fulfill({
        status: 200,
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(paged([TENANT])),
      }),
    );
    await mockJsonResponse(page, `**/api/v1/tenants/${TENANT.id}/status`, TENANT);
    // No provisioning record for a tenant that was seeded rather than provisioned.
    await mockJsonResponse(page, "**/api/v1/tenants/*/provisioning", "", { status: 404 });
  });

  test("the registry lists tenants and opens one", async ({ page }) => {
    await page.goto("/tenants");

    await expect(page.getByRole("heading", { name: "Registry", level: 1 })).toBeVisible();
    // Both a mobile card and a desktop row carry the name; the desktop row is last
    // in the DOM and is the visible one at the default viewport.
    await expect(page.getByText("Acme Corp").last()).toBeVisible();
  });

  test("entering a tenant exchanges a token and raises the acting banner", async ({ page }) => {
    const exchanges: Array<Record<string, unknown>> = [];
    await page.route("**/api/v1/identity/operator/token-exchange", async (route) => {
      exchanges.push(JSON.parse(route.request().postData() ?? "{}") as Record<string, unknown>);
      await route.fulfill({
        status: 200,
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(EXCHANGE_RESPONSE),
      });
    });

    await page.goto(`/tenants/${TENANT.id}`);

    await page.getByTestId("enter-tenant").click();
    await expect(page.getByRole("dialog")).toBeVisible();
    await page.getByLabel(/reason/i).fill("Support ticket 4821");
    await page.getByTestId("enter-tenant-confirm").click();

    // The banner renders from the in-memory acting session, on every page.
    const banner = page.getByTestId("acting-banner");
    await expect(banner).toBeVisible();
    await expect(banner).toContainText(TENANT.name);

    // The reason is mandatory and audited; the target travels in the body, never a header.
    expect(exchanges).toHaveLength(1);
    expect(exchanges[0]).toMatchObject({
      targetTenantId: TENANT.id,
      reason: "Support ticket 4821",
    });
  });

  test("exiting ends the grant and returns the operator to their own account", async ({ page }) => {
    await mockJsonResponse(page, "**/api/v1/identity/operator/token-exchange", EXCHANGE_RESPONSE);
    let ended = 0;
    await page.route("**/api/v1/identity/impersonation/end", async (route) => {
      ended += 1;
      await route.fulfill({
        status: 200,
        headers: { "Content-Type": "application/json" },
        // End returns no token: the operator's own session was never taken away.
        body: JSON.stringify({
          actorUserId: OPERATOR.sub,
          actorTenantId: OPERATOR.tenant,
          impersonatedUserId: TARGET_ADMIN.id,
          impersonatedTenantId: TENANT.id,
          endedAtUtc: new Date().toISOString(),
        }),
      });
    });

    await page.goto(`/tenants/${TENANT.id}`);
    await page.getByTestId("enter-tenant").click();
    await page.getByLabel(/reason/i).fill("Support ticket 4821");
    await page.getByTestId("enter-tenant-confirm").click();
    await expect(page.getByTestId("acting-banner")).toBeVisible();

    await page.getByTestId("acting-exit").click();

    await expect(page.getByTestId("acting-banner")).toHaveCount(0);
    expect(ended).toBe(1);
  });

  test("a reload drops the acting session — the token is never stored", async ({ page }) => {
    await mockJsonResponse(page, "**/api/v1/identity/operator/token-exchange", EXCHANGE_RESPONSE);

    await page.goto(`/tenants/${TENANT.id}`);
    await page.getByTestId("enter-tenant").click();
    await page.getByLabel(/reason/i).fill("Support ticket 4821");
    await page.getByTestId("enter-tenant-confirm").click();
    await expect(page.getByTestId("acting-banner")).toBeVisible();

    const stored = await page.evaluate(() =>
      Object.entries(localStorage).map(([key, value]) => `${key}=${String(value)}`).join("\n"),
    );
    expect(stored).not.toContain(EXCHANGE_RESPONSE.accessToken);

    await page.reload();

    await expect(page.getByTestId("enter-tenant")).toBeVisible();
    await expect(page.getByTestId("acting-banner")).toHaveCount(0);
  });

  test("a tenant user is told this tool is not theirs, on every route", async ({ page }) => {
    // Valid session, no operator grant: the sign-in worked, so the honest answer is
    // "wrong app", not a 403 panel inside a shell that cannot load (ADR-0008).
    await mockJsonResponse(page, "**/api/v1/identity/permissions", []);

    await page.goto("/tenants");
    await expect(
      page.getByRole("heading", { name: /console is for platform operators/i }),
    ).toBeVisible();
    await expect(page.getByRole("heading", { name: "Registry", level: 1 })).toHaveCount(0);

    // Not just the operator routes — the whole app is behind the same gate.
    await page.goto("/settings/profile");
    await expect(
      page.getByRole("heading", { name: /console is for platform operators/i }),
    ).toBeVisible();
  });
});
