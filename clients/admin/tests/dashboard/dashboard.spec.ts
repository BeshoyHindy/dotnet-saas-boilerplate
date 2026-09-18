import { expect, test } from "@playwright/test";
import { seedAuthedSession, TEST_USER } from "../helpers/auth-seed";
import { installAdminShellMocks, ADMIN_PERMS, paged } from "../helpers/shell-mocks";
import { mockJsonResponse } from "../helpers/api-mocks";

// DashboardPage ("/") is protected. The RouteGuard reads the in-memory
// permission set, which the auth context re-hydrates from
// /identity/permissions after mount — installAdminShellMocks echoes ADMIN_PERMS
// from that endpoint, so the seeded perms and the helper's perms must match.
//
// On load the page fires three queries:
//   GET /api/v1/tenants/?PageNumber=1&PageSize=1        (totalCount drives "Tenants")
//   GET /api/v1/identity/users/search?PageNumber=1&PageSize=1 (totalCount drives "Users")
//   GET /api/v1/identity/roles                           (array, drives "Roles")

const TENANTS_PAGE = paged(
  [
    {
      id: "acme",
      name: "Acme Corp",
      adminEmail: "admin@acme.com",
      isActive: true,
      validUpto: "2027-01-01T00:00:00Z",
    },
  ],
  { pageNumber: 1, pageSize: 1, totalCount: 12 },
);

const USERS_PAGE = paged(
  [
    {
      id: "u-1",
      userName: "alice",
      email: "alice@root.com",
      isActive: true,
      emailConfirmed: true,
    },
  ],
  { pageNumber: 1, pageSize: 1, totalCount: 7 },
);

const ROLES = [
  { id: "r-1", name: "SuperAdmin", description: "Full access", permissions: [] },
  { id: "r-2", name: "Support", description: "Read-only on tenants", permissions: [] },
  { id: "r-3", name: "Auditor", description: "Read-only on audit trails", permissions: [] },
];

test.beforeEach(async ({ page }) => {
  await seedAuthedSession(page, { ...TEST_USER, permissions: [...ADMIN_PERMS] });
  await installAdminShellMocks(page);

  // Page-specific mocks AFTER the shell mocks so they win.
  await mockJsonResponse(page, "**/api/v1/tenants**", TENANTS_PAGE);
  await mockJsonResponse(page, "**/api/v1/identity/users/search**", USERS_PAGE);
  await mockJsonResponse(page, "**/api/v1/identity/roles**", ROLES);
});

test.describe("admin dashboard", () => {
  test("greets the operator by first name in the hero heading", async ({ page }) => {
    await page.goto("/");

    // Seeded user is "Root Admin" → first name "Root". The EntityPageHeader h1
    // renders "Overview" + a muted ", Root" subspan, so match the accessible name.
    await expect(
      page.getByRole("heading", { name: /Overview,\s*Root/i }),
    ).toBeVisible({ timeout: 10_000 });
  });

  test("renders the three KPI tiles with values from the load endpoints", async ({ page }) => {
    await page.goto("/");

    // Scope to the page content region — the KPI labels ("Tenants", "Roles")
    // also appear in the sidebar nav, so an unscoped getByText collides.
    const main = page.getByRole("main");

    // KPI tile labels render as the Stat component's mono-caps ".meta" crumb.
    const kpiLabel = (text: string) =>
      main.locator("div.meta", { hasText: text });
    await expect(kpiLabel("Tenants")).toBeVisible({ timeout: 10_000 });
    await expect(kpiLabel("Users")).toBeVisible();
    await expect(kpiLabel("Roles")).toBeVisible();

    // Values: tenants totalCount = 12, users totalCount = 7, roles length = 3.
    await expect(main.getByText("12", { exact: true })).toBeVisible();
    await expect(main.getByText("7", { exact: true })).toBeVisible();
    await expect(main.getByText("3", { exact: true })).toBeVisible();
  });

  test("renders the entry-point pivot cards", async ({ page }) => {
    await page.goto("/");

    // The sidebar nav lives OUTSIDE <main>, so scoping to the content region
    // isolates the four pivot-card links from the nav's own route links.
    const main = page.getByRole("main");
    await expect(main.getByText("Entry points")).toBeVisible({ timeout: 10_000 });

    await expect(main.getByRole("link", { name: /Tenants/ })).toBeVisible();
    await expect(main.getByRole("link", { name: /Users/ })).toBeVisible();
    await expect(main.getByRole("link", { name: /Roles/ })).toBeVisible();
    await expect(main.getByRole("link", { name: /Audits/ })).toBeVisible();
  });
});
