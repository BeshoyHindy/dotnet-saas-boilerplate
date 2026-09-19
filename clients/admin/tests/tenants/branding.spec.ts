import { expect, test } from "@playwright/test";
import { mockJsonResponse } from "../helpers/api-mocks";
import { seedAuthedSession, TEST_USER } from "../helpers/auth-seed";

const TENANT_ID = "acme";

// RouteGuard on /tenants/:id requires Tenants.View. The theme endpoints are
// current-tenant-scoped server-side (no tenant header override since the
// root operator's `tenant` header was retired), so ViewTheme / UpdateTheme
// permissions are irrelevant here: the branding card never calls them on
// this page.
const ROOT_PERMS = ["Permissions.Tenants.View"];

const TENANT = {
  id: TENANT_ID,
  name: "Acme Corp",
  adminEmail: "admin@acme.com",
  isActive: true,
  validUpto: "2027-01-01T00:00:00Z",
  issuer: "acme.example.com",
};

const PROVISIONING = {
  status: "Completed",
  currentStep: "CacheWarm",
  correlationId: "abc-123",
  steps: [],
  startedUtc: "2026-05-10T10:00:00Z",
  completedUtc: "2026-05-10T10:00:08Z",
  error: null,
};

test.beforeEach(async ({ page }) => {
  await seedAuthedSession(page, { ...TEST_USER, permissions: ROOT_PERMS });
  await mockJsonResponse(page, "**/api/v1/identity/profile", {
    id: TEST_USER.sub,
    email: TEST_USER.email,
    isActive: true,
    emailConfirmed: true,
  });
  await mockJsonResponse(page, "**/api/v1/identity/permissions", []);
  await mockJsonResponse(page, `**/api/v1/tenants/${TENANT_ID}/status`, TENANT);
  await mockJsonResponse(
    page,
    `**/api/v1/tenants/${TENANT_ID}/provisioning`,
    PROVISIONING,
  );
  // Active grants list — not under test here, return empty.
  await mockJsonResponse(page, "**/api/v1/identity/impersonation/grants**", []);
});

test.describe("tenant branding card", () => {
  test("renders a read-only notice and never calls the theme endpoints", async ({ page }) => {
    let themeCalled = false;
    page.on("request", (r) => {
      if (r.url().includes("/api/v1/tenants/theme")) themeCalled = true;
    });

    await page.goto(`/tenants/${TENANT_ID}`);

    const branding = page.locator("section, div").filter({ hasText: "Branding" }).first();
    await expect(branding).toBeVisible({ timeout: 10_000 });

    await expect(
      page.getByText(
        /editing another tenant's branding needs operator token exchange, which is not available yet/i,
      ),
    ).toBeVisible();

    // Nothing this card would have rendered when editable.
    await expect(page.getByRole("button", { name: /save branding/i })).not.toBeVisible();
    await expect(page.getByRole("button", { name: /reset (branding )?to defaults/i })).not.toBeVisible();
    await expect(page.getByLabel("Logo URL", { exact: true })).not.toBeVisible();

    expect(themeCalled).toBe(false);
  });

  test("does not PUT or POST/DELETE the theme even after the page settles", async ({ page }) => {
    let putSent = false;
    let resetSent = false;
    page.on("request", (r) => {
      if (r.url().endsWith("/api/v1/tenants/theme") && r.method() === "PUT") putSent = true;
      if (r.url().endsWith("/api/v1/tenants/theme/reset") && r.method() === "POST") resetSent = true;
    });

    await page.goto(`/tenants/${TENANT_ID}`);
    await expect(
      page.getByText(/editing another tenant's branding needs operator token exchange/i),
    ).toBeVisible({ timeout: 10_000 });

    // Give any stray fetch a chance to fire before asserting its absence.
    await page.waitForTimeout(250);

    expect(putSent).toBe(false);
    expect(resetSent).toBe(false);
  });
});
