import { expect, test } from "@playwright/test";
import { mockJsonResponse } from "../helpers/api-mocks";
import { seedAuthedSession, TEST_USER } from "../helpers/auth-seed";

const TENANT_ID = "acme";

// RouteGuard on /tenants/:id requires Tenants.View. The theme endpoints are current-tenant
// scoped server-side, so this card only becomes editable once the operator has ENTERED the
// tenant (issue #9's token exchange) — which needs the root-only cross-tenant permission.
const ROOT_PERMS = [
  "Permissions.Tenants.View",
  "Permissions.Platform.Users.Impersonate",
];

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

const PALETTE = {
  primary: "#2563EB",
  secondary: "#0F172A",
  tertiary: "#6366F1",
  background: "#F8FAFC",
  surface: "#FFFFFF",
  error: "#DC2626",
  warning: "#F59E0B",
  success: "#16A34A",
  info: "#0284C7",
};

const THEME = {
  lightPalette: PALETTE,
  darkPalette: PALETTE,
  brandAssets: { logoUrl: null, logoDarkUrl: null, faviconUrl: null },
  typography: {
    fontFamily: "Inter",
    headingFontFamily: "Inter",
    fontSizeBase: 16,
    lineHeightBase: 1.5,
  },
  layout: { borderRadius: "0.5rem", defaultElevation: 1 },
  isDefault: true,
};

const EXCHANGE_RESPONSE = {
  accessToken: "acting.token.value",
  accessTokenExpiresAt: new Date(Date.now() + 15 * 60_000).toISOString(),
  targetTenantId: TENANT_ID,
  targetUserId: "u-acme-admin",
  targetUserName: "admin@acme.com",
  actorUserId: TEST_USER.sub,
  actorTenantId: "root",
  grantId: "g-exchange-1",
  jti: "jti-exchange-1",
};

test.beforeEach(async ({ page }) => {
  await seedAuthedSession(page, { ...TEST_USER, permissions: ROOT_PERMS });
  await mockJsonResponse(page, "**/api/v1/identity/profile", {
    id: TEST_USER.sub,
    email: TEST_USER.email,
    isActive: true,
    emailConfirmed: true,
  });
  await mockJsonResponse(page, "**/api/v1/identity/permissions", ROOT_PERMS);
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
  test("offers the way in and never calls the theme endpoints until acting", async ({ page }) => {
    let themeCalled = false;
    page.on("request", (r) => {
      if (r.url().includes("/api/v1/tenants/theme")) themeCalled = true;
    });

    await page.goto(`/tenants/${TENANT_ID}`);

    const branding = page.locator("section, div").filter({ hasText: "Branding" }).first();
    await expect(branding).toBeVisible({ timeout: 10_000 });

    await expect(page.getByText(/enter this tenant to edit its branding/i)).toBeVisible();
    await expect(page.getByTestId("branding-enter-tenant")).toBeVisible();

    // Nothing this card renders when editable.
    await expect(page.getByRole("button", { name: /save branding/i })).not.toBeVisible();
    await expect(page.getByRole("button", { name: /reset (branding )?to defaults/i })).not.toBeVisible();
    await expect(page.getByLabel("Logo URL", { exact: true })).not.toBeVisible();

    // Reading the theme with the operator's own token would answer for the OPERATOR's tenant,
    // and saving would overwrite their branding while looking like it edited Acme's.
    expect(themeCalled).toBe(false);
  });

  test("becomes editable after entering the tenant", async ({ page }) => {
    await mockJsonResponse(
      page,
      "**/api/v1/identity/operator/token-exchange",
      EXCHANGE_RESPONSE,
      { method: "POST" },
    );
    await mockJsonResponse(page, "**/api/v1/tenants/theme", THEME);

    await page.goto(`/tenants/${TENANT_ID}`);
    await page.getByTestId("branding-enter-tenant").click();

    await page.getByLabel("Reason").fill("Ticket #4821 — fixing brand colours");
    await page.getByTestId("enter-tenant-confirm").click();

    // The acting banner is the operator's standing reminder of whose data this is.
    await expect(page.getByTestId("acting-banner")).toBeVisible({ timeout: 10_000 });

    // …and the editor is live, fed by the acting token's tenant.
    await expect(page.getByRole("button", { name: /save branding/i })).toBeVisible();
    await expect(page.getByLabel("Logo URL", { exact: true })).toBeVisible();
  });

  test("sends the theme request with the acting token, not the operator's", async ({ page }) => {
    await mockJsonResponse(
      page,
      "**/api/v1/identity/operator/token-exchange",
      EXCHANGE_RESPONSE,
      { method: "POST" },
    );
    await mockJsonResponse(page, "**/api/v1/tenants/theme", THEME);

    const themeAuthHeaders: string[] = [];
    page.on("request", (r) => {
      if (r.url().endsWith("/api/v1/tenants/theme")) {
        themeAuthHeaders.push(r.headers()["authorization"] ?? "");
      }
    });

    await page.goto(`/tenants/${TENANT_ID}`);
    await page.getByTestId("branding-enter-tenant").click();
    await page.getByLabel("Reason").fill("Ticket #4821 — fixing brand colours");
    await page.getByTestId("enter-tenant-confirm").click();

    await expect(page.getByRole("button", { name: /save branding/i })).toBeVisible({
      timeout: 10_000,
    });

    expect(themeAuthHeaders.length).toBeGreaterThan(0);
    for (const header of themeAuthHeaders) {
      expect(header).toBe(`Bearer ${EXCHANGE_RESPONSE.accessToken}`);
    }
  });
});
