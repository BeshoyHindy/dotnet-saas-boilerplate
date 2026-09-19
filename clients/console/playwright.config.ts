import { defineConfig, devices } from "@playwright/test";

/**
 * Playwright config for the console.
 *
 * The suite here is a deliberately SMALL smoke suite (ADR-0004): sign-in, user CRUD,
 * and an operator entering a tenant — the three journeys that must not break silently.
 * Everything below page level belongs in the Vitest units next to the source.
 *
 * Tests run against a Vite dev server on port 5174 with API calls intercepted via
 * `page.route()`, so no backend, no database and no seeding are involved.
 *
 * Usage:
 *   pnpm test:e2e                  # headless
 *   pnpm test:e2e -- --ui          # interactive runner
 *   pnpm test:e2e -- --headed      # watch the browser drive
 *   pnpm test:e2e -- login.spec    # filter by file
 */
export default defineConfig({
  testDir: "./tests",
  fullyParallel: true,
  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 2 : 0,
  workers: process.env.CI ? 2 : undefined,
  reporter: process.env.CI ? [["github"], ["html", { open: "never" }]] : "list",

  use: {
    baseURL: "http://localhost:5174",
    trace: "on-first-retry",
    actionTimeout: 10_000,
    navigationTimeout: 15_000,
  },

  projects: [
    {
      name: "chromium",
      use: { ...devices["Desktop Chrome"] },
    },
  ],

  // Boot the Vite dev server before any test runs. `reuseExistingServer` means a
  // re-run picks up an already-running dev server (faster local iteration).
  webServer: {
    command: "pnpm dev",
    url: "http://localhost:5174",
    reuseExistingServer: !process.env.CI,
    timeout: 60_000,
    stdout: "ignore",
    stderr: "pipe",
  },
});
