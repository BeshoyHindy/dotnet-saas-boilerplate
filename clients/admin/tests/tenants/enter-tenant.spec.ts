import { expect, test } from "@playwright/test";
import { mockJsonResponse, mockProblemDetails } from "../helpers/api-mocks";
import { seedAuthedSession, TEST_USER } from "../helpers/auth-seed";
import { installAdminShellMocks, ADMIN_PERMS } from "../helpers/shell-mocks";

/**
 * "Enter tenant" — the operator token exchange from the operator's side (ADR-0002, issue #9).
 *
 * What matters here is what the operator can see and undo: the banner while acting, the exchanged
 * token being the one on the wire, and the fall back to their own session when it dies.
 */

const TENANT_ID = "acme";

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
  await seedAuthedSession(page, { ...TEST_USER, permissions: [...ADMIN_PERMS] });
  await installAdminShellMocks(page);
  await mockJsonResponse(page, `**/api/v1/tenants/${TENANT_ID}/status`, TENANT);
  await mockJsonResponse(page, `**/api/v1/tenants/${TENANT_ID}/provisioning`, PROVISIONING);
  await mockJsonResponse(page, "**/api/v1/identity/impersonation/grants**", []);
  await mockJsonResponse(page, "**/api/v1/tenants/theme", { isDefault: true });
});

test.describe("enter tenant", () => {
  test("exchanges a token and shows the acting banner", async ({ page }) => {
    await mockJsonResponse(
      page,
      "**/api/v1/identity/operator/token-exchange",
      EXCHANGE_RESPONSE,
      { method: "POST" },
    );

    await page.goto(`/tenants/${TENANT_ID}`);
    await page.getByTestId("enter-tenant").click();

    // The reason is mandatory — it is the only part of the audit row a human writes.
    await expect(page.getByTestId("enter-tenant-confirm")).toBeDisabled();
    await page.getByLabel("Reason").fill("Ticket #4821 — checking their setup");
    await page.getByTestId("enter-tenant-confirm").click();

    const banner = page.getByTestId("acting-banner");
    await expect(banner).toBeVisible({ timeout: 10_000 });
    await expect(banner).toContainText("Acme Corp");
    await expect(banner).toContainText("admin@acme.com");
  });

  test("sends the exchanged token on subsequent requests", async ({ page }) => {
    await mockJsonResponse(
      page,
      "**/api/v1/identity/operator/token-exchange",
      EXCHANGE_RESPONSE,
      { method: "POST" },
    );

    const grantsAuth: string[] = [];
    page.on("request", (r) => {
      if (r.url().includes("/api/v1/identity/impersonation/grants")) {
        grantsAuth.push(r.headers()["authorization"] ?? "");
      }
    });

    await page.goto(`/tenants/${TENANT_ID}`);
    await page.getByTestId("enter-tenant").click();
    await page.getByLabel("Reason").fill("Ticket #4821 — checking their setup");
    await page.getByTestId("enter-tenant-confirm").click();
    await expect(page.getByTestId("acting-banner")).toBeVisible({ timeout: 10_000 });

    // Requests made after entering carry the acting token; the tenant it names is the only
    // scope the server honours (ADR-0002) — the client never sends a tenant itself.
    await expect
      .poll(() => grantsAuth.at(-1))
      .toBe(`Bearer ${EXCHANGE_RESPONSE.accessToken}`);
  });

  test("Exit ends the grant and returns the operator to their own session", async ({ page }) => {
    await mockJsonResponse(
      page,
      "**/api/v1/identity/operator/token-exchange",
      EXCHANGE_RESPONSE,
      { method: "POST" },
    );
    await mockJsonResponse(
      page,
      "**/api/v1/identity/impersonation/end",
      {
        actorUserId: TEST_USER.sub,
        actorTenantId: "root",
        impersonatedUserId: EXCHANGE_RESPONSE.targetUserId,
        impersonatedTenantId: TENANT_ID,
        endedAtUtc: new Date().toISOString(),
      },
      { method: "POST" },
    );

    await page.goto(`/tenants/${TENANT_ID}`);
    await page.getByTestId("enter-tenant").click();
    await page.getByLabel("Reason").fill("Ticket #4821 — checking their setup");
    await page.getByTestId("enter-tenant-confirm").click();
    await expect(page.getByTestId("acting-banner")).toBeVisible({ timeout: 10_000 });

    await page.getByTestId("acting-exit").click();

    // No re-login, no token dance: the operator's own session was never touched.
    await expect(page.getByTestId("acting-banner")).toBeHidden();
    await expect(page.getByTestId("enter-tenant")).toBeVisible();
  });

  test("a revoked acting token drops the operator back with a notice", async ({ page }) => {
    await mockJsonResponse(
      page,
      "**/api/v1/identity/operator/token-exchange",
      EXCHANGE_RESPONSE,
      { method: "POST" },
    );

    await page.goto(`/tenants/${TENANT_ID}`);
    await page.getByTestId("enter-tenant").click();
    await page.getByLabel("Reason").fill("Ticket #4821 — checking their setup");
    await page.getByTestId("enter-tenant-confirm").click();
    await expect(page.getByTestId("acting-banner")).toBeVisible({ timeout: 10_000 });

    // The grant is revoked elsewhere: the JWT validation hook now 401s the acting token. There
    // is nothing to refresh (it is access-only), so the client must fall back, not sign out.
    await mockProblemDetails(page, "**/api/v1/tenants/*/status", 401, {
      title: "Unauthorized",
    });
    await page.reload();

    await expect(page.getByTestId("acting-banner")).toBeHidden({ timeout: 10_000 });
  });

  test("the exchanged token is never written to localStorage", async ({ page }) => {
    await mockJsonResponse(
      page,
      "**/api/v1/identity/operator/token-exchange",
      EXCHANGE_RESPONSE,
      { method: "POST" },
    );

    await page.goto(`/tenants/${TENANT_ID}`);
    await page.getByTestId("enter-tenant").click();
    await page.getByLabel("Reason").fill("Ticket #4821 — checking their setup");
    await page.getByTestId("enter-tenant-confirm").click();
    await expect(page.getByTestId("acting-banner")).toBeVisible({ timeout: 10_000 });

    const persisted = await page.evaluate(() => JSON.stringify(window.localStorage));
    expect(persisted).not.toContain(EXCHANGE_RESPONSE.accessToken);

    // …and a reload therefore returns the operator to their own account.
    await page.reload();
    await expect(page.getByTestId("acting-banner")).toBeHidden({ timeout: 10_000 });
  });
});
