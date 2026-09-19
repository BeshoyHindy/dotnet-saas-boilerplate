import { expect, test } from "@playwright/test";
import { mockJsonResponse } from "../helpers/api-mocks";
import { installShellMocks, paged } from "../helpers/shell-mocks";
import { seedAuthedSession, TEST_USER } from "../helpers/auth-seed";

test.describe("overview (/)", () => {
  test.beforeEach(async ({ page }) => {
    await seedAuthedSession(page, TEST_USER);
    await installShellMocks(page);
    await mockJsonResponse(page, "**/api/v1/audits**", paged([]));
  });

  test("renders the greeting header and the stat card", async ({ page }) => {
    await page.goto("/");
    await expect(page.getByRole("heading", { name: /good (morning|afternoon|evening), alice/i })).toBeVisible();
    await expect(page.getByText("Valid for", { exact: true })).toBeVisible();
  });

  test("renders the recent-audits section", async ({ page }) => {
    await page.goto("/");
    await expect(page.getByText("Recent audits", { exact: true })).toBeVisible();
  });

  test("Valid-for card reflects an in-grace tenant", async ({ page }) => {
    await mockJsonResponse(page, "**/api/v1/tenants/me/status**", {
      id: "acme",
      name: "Acme Corp",
      isActive: true,
      validUpto: new Date(Date.now() - 2 * 24 * 60 * 60 * 1000).toISOString(),
      hasConnectionString: false,
      adminEmail: "admin@acme.com",
      issuer: null,
      expiryState: "InGrace",
      graceEndsUtc: new Date(Date.now() + 5 * 24 * 60 * 60 * 1000).toISOString(),
    });
    await page.goto("/");
    await expect(page.getByText("Valid for", { exact: true })).toBeVisible();
    // Grace surfaces the grace-end caption on the stat card.
    await expect(page.getByText(/grace ends/i)).toBeVisible();
  });

  test("Valid-for card reflects an expired tenant", async ({ page }) => {
    await mockJsonResponse(page, "**/api/v1/tenants/me/status**", {
      id: "acme",
      name: "Acme Corp",
      isActive: false,
      validUpto: new Date(Date.now() - 30 * 24 * 60 * 60 * 1000).toISOString(),
      hasConnectionString: false,
      adminEmail: "admin@acme.com",
      issuer: null,
      expiryState: "Expired",
      graceEndsUtc: new Date(Date.now() - 16 * 24 * 60 * 60 * 1000).toISOString(),
    });
    await page.goto("/");
    await expect(page.getByText("Valid for", { exact: true })).toBeVisible();
    // The stat card reads "Expired" rather than a healthy day count.
    await expect(page.getByText("Expired", { exact: true })).toBeVisible();
  });
});
