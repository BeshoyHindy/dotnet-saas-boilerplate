import { expect, test } from "@playwright/test";
import { mockJsonResponse } from "../helpers/api-mocks";
import { seedAuthedSession } from "../helpers/auth-seed";
import { installShellMocks, paged } from "../helpers/shell-mocks";

/**
 * Operator enters a tenant — the third smoke journey (ADR-0004).
 *
 * One console serves tenant users and root operators, so this exercises the whole
 * seam: the permission-gated /tenants route resolves for an operator, the tenant
 * page offers "Impersonate user", and confirming swaps the session in place (no
 * hand-off to a second app) and lands on the console's own overview.
 *
 * NOTE (issue #9, operator token exchange): today "entering a tenant" is the
 * impersonation grant — `POST /identity/impersonation/start`, which mints an
 * access-only token carrying `act_sub`. When #9 lands, the operator exchange
 * replaces that call and THIS TEST is the one that has to follow it: the user
 * picker (which cannot list another tenant's users until the exchange exists,
 * ADR-0002) and the mocked start endpoint below are what change.
 */

const OPERATOR = {
  sub: "op-1",
  email: "root@boilerplate.local",
  firstName: "Root",
  lastName: "Operator",
  tenant: "root",
};

const OPERATOR_PERMISSIONS = [
  "Permissions.Tenants.View",
  "Permissions.Users.Impersonate",
  "Permissions.Impersonation.View",
];

const TENANT = {
  id: "acme",
  name: "Acme Corp",
  adminEmail: "admin@acme.com",
  isActive: true,
  validUpto: new Date(Date.now() + 365 * 24 * 60 * 60 * 1000).toISOString(),
  hasConnectionString: false,
  issuer: null,
  expiryState: "Active",
  graceEndsUtc: new Date(Date.now() + 372 * 24 * 60 * 60 * 1000).toISOString(),
};

const TARGET_USER = {
  id: "00000000-0000-0000-0000-0000000000b0",
  userName: "bob",
  firstName: "Bob",
  lastName: "Patel",
  email: "bob@acme.com",
  phoneNumber: null,
  imageUrl: null,
  twoFactorEnabled: false,
  isActive: true,
  emailConfirmed: true,
};

/** A JWT-shaped impersonation token: the app reads `act_sub` to show the banner. */
function impersonationToken(): string {
  const b64url = (obj: unknown) =>
    btoa(JSON.stringify(obj)).replace(/=+$/, "").replace(/\+/g, "-").replace(/\//g, "_");
  const payload = {
    sub: TARGET_USER.id,
    email: TARGET_USER.email,
    name: "Bob Patel",
    tenant: TENANT.id,
    act_sub: OPERATOR.sub,
    act_tenant: OPERATOR.tenant,
    act_name: "Root Operator",
    exp: Math.floor(Date.now() / 1000) + 900,
    iat: Math.floor(Date.now() / 1000),
  };
  return [b64url({ alg: "HS256", typ: "JWT" }), b64url(payload), "sig"].join(".");
}

test.describe("operator enters a tenant", () => {
  test.beforeEach(async ({ page }) => {
    await seedAuthedSession(page, OPERATOR);
    await installShellMocks(page);
    // The shell mocks grant nothing; an operator holds the tenant + impersonation set.
    await mockJsonResponse(page, "**/api/v1/identity/permissions", OPERATOR_PERMISSIONS);
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

  test("impersonating a user swaps the session in place and lands on the overview", async ({
    page,
  }) => {
    await mockJsonResponse(page, "**/api/v1/identity/users/search**", paged([TARGET_USER]));
    await mockJsonResponse(page, "**/api/v1/identity/impersonation/start", {
      accessToken: impersonationToken(),
      accessTokenExpiresAt: new Date(Date.now() + 900_000).toISOString(),
      actorUserId: OPERATOR.sub,
      actorTenantId: OPERATOR.tenant,
      impersonatedUserId: TARGET_USER.id,
      impersonatedTenantId: TENANT.id,
    });

    await page.goto(`/tenants/${TENANT.id}`);

    await page.getByRole("button", { name: /impersonate user/i }).click();
    await expect(page.getByRole("dialog")).toBeVisible();

    await page.getByRole("button", { name: /bob patel/i }).click();
    await page.getByLabel(/reason/i).fill("Support ticket 4821");
    await page.getByRole("button", { name: /start \d+-min impersonation/i }).click();

    // In place: the console navigates to its own overview, and the banner that only
    // renders for a token carrying `act_sub` appears.
    await expect(page).toHaveURL(/\/$/);
    await expect(page.getByRole("status").filter({ hasText: /impersonat/i })).toBeVisible();
  });

  test("a tenant user without the operator permissions is refused the registry", async ({
    page,
  }) => {
    await mockJsonResponse(page, "**/api/v1/identity/permissions", []);

    await page.goto("/tenants");

    await expect(page.getByText(/don’t have|do not have|forbidden|access/i).first()).toBeVisible();
    await expect(page.getByRole("heading", { name: "Registry", level: 1 })).toHaveCount(0);
  });
});
