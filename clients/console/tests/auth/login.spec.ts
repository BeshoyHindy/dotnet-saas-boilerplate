import { expect, test } from "@playwright/test";
import { mockJsonResponse, mockProblemDetails } from "../helpers/api-mocks";

// The console login page (rebuilt to the dentalOS card layout): Boilerplate logo
// lockup + ".NET 10 Starter Kit" caption, and a tenant/email/password card.

const TOKEN_RESPONSE = {
  accessToken: "header.payload.sig",
  refreshToken: "refresh",
  accessTokenExpiresAt: new Date(Date.now() + 3_600_000).toISOString(),
  refreshTokenExpiresAt: new Date(Date.now() + 7_200_000).toISOString(),
};

/** Force the runtime config to a deterministic value per test. */
async function setConfig(page: import("@playwright/test").Page) {
  await page.route("**/config.json", (route) =>
    route.fulfill({
      status: 200,
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ apiBase: "", defaultTenant: "root" }),
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

  test("renders the tenant + email + password fields", async ({ page }) => {
    await page.goto("/login");
    await expect(page.getByLabel("Tenant")).toBeVisible();
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

  test("submit is disabled until tenant + email + password are filled", async ({ page }) => {
    await page.goto("/login");
    const submit = page.getByRole("button", { name: /^sign in$/i });
    // Tenant defaults to "root"; fill the rest to enable.
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
    await page.getByLabel("Tenant").fill("acme");
    await page.getByLabel("Email").fill("alice@acme.com");
    await page.getByLabel("Password", { exact: true }).fill("Password123!");

    const reqPromise = page.waitForRequest(
      (r) => r.url().includes("/auth/token") && r.method() === "POST",
    );
    await page.getByRole("button", { name: /^sign in$/i }).click();
    const req = await reqPromise;

    expect(req.url()).toContain("/api/v1/tenants/acme/auth/token");
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
