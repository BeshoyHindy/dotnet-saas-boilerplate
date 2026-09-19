import { expect, test } from "@playwright/test";
import { mockJsonResponse, mockProblemDetails } from "../helpers/api-mocks";

// The console login page: Boilerplate logo lockup + ".NET 10 Starter Kit" caption, and an
// email/password card. There is NO TENANT FIELD (ADR-0008): operators live in the root
// tenant, so this app always signs in to `defaultTenant`.

const TOKEN_RESPONSE = {
  accessToken: "header.payload.sig",
  refreshToken: "refresh",
  accessTokenExpiresAt: new Date(Date.now() + 3_600_000).toISOString(),
  refreshTokenExpiresAt: new Date(Date.now() + 7_200_000).toISOString(),
};

/** Force the runtime config to a deterministic value per test. */
async function setConfig(
  page: import("@playwright/test").Page,
  overrides: Record<string, unknown> = {},
) {
  await page.route("**/config.json", (route) =>
    route.fulfill({
      status: 200,
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ apiBase: "", defaultTenant: "root", ...overrides }),
    }),
  );
}

test.describe("login — page chrome", () => {
  test.beforeEach(async ({ page }) => {
    await setConfig(page);
  });

  test("renders the Boilerplate wordmark lockup with the .NET 10 caption", async ({ page }) => {
    await page.goto("/login");
    await expect(page.getByText("Boilerplate").first()).toBeVisible();
    await expect(page.getByText(/\.NET 10 Starter Kit/i)).toBeVisible();
    await expect(page.getByRole("heading", { name: /welcome back/i })).toBeVisible();
    await expect(page.getByText(/sign in to your account/i)).toBeVisible();
  });

  test("renders email + password, and no tenant field at all", async ({ page }) => {
    await page.goto("/login");
    // An operator would only ever type "root" here, so the form does not ask.
    await expect(page.getByLabel("Tenant")).toHaveCount(0);
    await expect(page.getByLabel("Workspace")).toHaveCount(0);
    await expect(page.getByLabel("Email")).toBeVisible();
    await expect(page.getByLabel("Password", { exact: true })).toBeVisible();
    await expect(page.getByRole("link", { name: /forgot/i })).toHaveAttribute("href", "/forgot-password");
  });

  test("password visibility toggle flips the input type", async ({ page }) => {
    await page.goto("/login");
    const pwd = page.getByLabel("Password", { exact: true });
    await expect(pwd).toHaveAttribute("type", "password");
    await page.getByRole("button", { name: /show password/i }).click();
    await expect(pwd).toHaveAttribute("type", "text");
    await page.getByRole("button", { name: /hide password/i }).click();
    await expect(pwd).toHaveAttribute("type", "password");
  });

  test("submit is disabled until email + password are filled", async ({ page }) => {
    await page.goto("/login");
    const submit = page.getByRole("button", { name: /^sign in$/i });
    await expect(submit).toBeDisabled();
    await page.getByLabel("Email").fill("alice@acme.com");
    await page.getByLabel("Password", { exact: true }).fill("secret123");
    await expect(submit).toBeEnabled();
  });
});

test.describe("login — manual sign in", () => {
  test.beforeEach(async ({ page }) => {
    await setConfig(page);
    await mockJsonResponse(page, "**/api/v1/tenants/*/auth/token", TOKEN_RESPONSE);
  });

  test("POSTs credentials to the tenant auth route", async ({ page }) => {
    await page.goto("/login");
    await page.getByLabel("Email").fill("alice@acme.com");
    await page.getByLabel("Password", { exact: true }).fill("Password123!");

    const reqPromise = page.waitForRequest(
      (r) => r.url().includes("/auth/token") && r.method() === "POST",
    );
    await page.getByRole("button", { name: /^sign in$/i }).click();
    const req = await reqPromise;

    // Always the configured tenant, never one the operator typed.
    expect(req.url()).toContain("/api/v1/tenants/root/auth/token");
    expect(req.headers().tenant).toBeUndefined();
    expect(JSON.parse(req.postData() ?? "{}")).toMatchObject({
      email: "alice@acme.com",
      password: "Password123!",
    });
  });

  test("surfaces a server error without leaving the page", async ({ page }) => {
    await mockProblemDetails(page, "**/api/v1/tenants/*/auth/token", 401, {
      title: "Unauthorized",
      detail: "Invalid credentials.",
    });
    await page.goto("/login");
    await page.getByLabel("Email").fill("alice@acme.com");
    await page.getByLabel("Password", { exact: true }).fill("wrongpw");
    await page.getByRole("button", { name: /^sign in$/i }).click();

    await expect(page.getByRole("alert")).toContainText(/invalid credentials/i);
    await expect(page.getByRole("heading", { name: /welcome back/i })).toBeVisible();
  });
});

test.describe("login — the demo operator affordance", () => {
  test("is absent unless demo mode is on", async ({ page }) => {
    await setConfig(page);
    await page.goto("/login");
    await expect(page.getByTestId("demo-operator-fill")).toHaveCount(0);
  });

  test("prefills the operator email and never signs in", async ({ page }) => {
    // The seeded root admin's password is Seed__DefaultAdminPassword, a different
    // parameter from the demo tenants' shared one — so the console has nothing to sign
    // in WITH, and must not pretend otherwise (ADR-0008).
    await setConfig(page, { demoMode: true, demoOperatorEmail: "admin@root.com" });
    let posted = 0;
    await page.route("**/api/v1/tenants/*/auth/token", async (route) => {
      posted += 1;
      await route.fulfill({ status: 200, body: "{}" });
    });
    await page.goto("/login");

    await page.getByTestId("demo-operator-fill").click();

    await expect(page.getByLabel("Email")).toHaveValue("admin@root.com");
    await expect(page.getByLabel("Password", { exact: true })).toHaveValue("");
    await expect(page.getByRole("status").first()).toContainText(/enter its password/i);
    expect(posted).toBe(0);
  });
});
