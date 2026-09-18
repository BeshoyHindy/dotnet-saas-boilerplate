import { expect, test, type Page } from "@playwright/test";
import { mockJsonResponse } from "../helpers/api-mocks";
import { seedAuthedSession, TEST_USER } from "../helpers/auth-seed";
import { installShellMocks, paged } from "../helpers/shell-mocks";

// Trash is tabbed, but Files is the only tab today. Default tab = Files →
// GET /api/v1/files/trash. The trash row VM only reads
// { id, originalFileName, contentType, deletedOnUtc, deletedBy }.
function trashedFile(over: Record<string, unknown> = {}) {
  return {
    id: "f-1",
    ownerType: "User",
    ownerId: "u-1",
    originalFileName: "invoice.pdf",
    contentType: "application/pdf",
    sizeBytes: 1024,
    visibility: "Private",
    status: "Available",
    scanStatus: 0,
    createdAtUtc: "2026-05-01T00:00:00.000Z",
    createdByUserId: "11111111-2222-3333-4444-555555555555",
    deletedOnUtc: "2026-05-20T12:00:00.000Z",
    deletedBy: "11111111-2222-3333-4444-555555555555",
    ...over,
  };
}

// Trash tabs are permission-gated (mirrors src/lib/trash-permissions.ts). The
// dashboard reads the user's permission set from GET /identity/permissions, so
// tests grant tabs by re-mocking that endpoint AFTER installShellMocks (which
// defaults it to []); Playwright matches the most-recently-registered route.
const TRASH_PERMS = {
  files: "Permissions.Files.ViewTrash",
} as const;

const ALL_TRASH_PERMS = Object.values(TRASH_PERMS);

async function grantPermissions(page: Page, perms: readonly string[]): Promise<void> {
  await mockJsonResponse(page, "**/api/v1/identity/permissions", perms);
}

test.beforeEach(async ({ page }) => {
  await seedAuthedSession(page, TEST_USER);
  await installShellMocks(page);
  // Default: the user can reach every trash tab. Gating-specific tests override.
  await grantPermissions(page, ALL_TRASH_PERMS);
});

test.describe("system/trash", () => {
  test("renders the 'Recycle bin' heading + a trashed file row (default tab)", async ({
    page,
  }) => {
    await mockJsonResponse(
      page,
      "**/api/v1/files/trash**",
      paged([trashedFile({ originalFileName: "invoice.pdf" })], { totalCount: 1 }),
    );

    await page.goto("/system/trash");

    await expect(
      page.getByRole("heading", { name: "Recycle bin", level: 1 }),
    ).toBeVisible();

    // Row title renders in the hidden mobile card AND the desktop row →
    // assert on the last (visible desktop) occurrence.
    await expect(page.getByText("invoice.pdf").last()).toBeVisible();
    await expect(page.getByText(/1 files in trash/i)).toBeVisible();
    // Each row has a Restore action.
    await expect(page.getByRole("button", { name: /restore/i }).last()).toBeVisible();
  });

  test("renders the empty state for the files tab", async ({ page }) => {
    await mockJsonResponse(page, "**/api/v1/files/trash**", paged([], { totalCount: 0 }));

    await page.goto("/system/trash");

    await expect(
      page.getByRole("heading", { name: /the files trash is empty/i }),
    ).toBeVisible();
    await expect(page.getByRole("button", { name: /back to files/i })).toBeVisible();
  });

  test("shows a no-access empty state when the user has no trash permissions", async ({
    page,
  }) => {
    await grantPermissions(page, []);

    await page.goto("/system/trash");

    await expect(
      page.getByRole("heading", { name: /no recycle bins available/i }),
    ).toBeVisible();
    // No tab rail at all.
    await expect(
      page.getByRole("navigation", { name: /trash sections/i }),
    ).toHaveCount(0);
  });
});
