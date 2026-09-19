import { expect, test } from "@playwright/test";
import { mockJsonResponse, mockProblemDetails } from "../helpers/api-mocks";

// The dashboard login page: Boilerplate logo lockup + ".NET 10 Starter Kit" caption, and
// an email/password card. NOBODY TYPES A TENANT (ADR-0008) — it is resolved from the URL,
// the hostname, the last sign-in on this device, or the configured default, and the
// "Workspace" field only appears when the arrival did not name one.

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

  test("renders email + password, with Workspace below them and no Tenant field", async ({
    page,
  }) => {
    await page.goto("/login");
    await expect(page.getByLabel("Tenant")).toHaveCount(0);
    await expect(page.getByLabel("Email")).toBeVisible();
    await expect(page.getByLabel("Workspace")).toBeVisible();
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
    // The workspace is already resolved; only the credentials are missing.
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
    await page.getByLabel("Workspace").fill("acme");
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

test.describe("login — the workspace is resolved, never typed", () => {
  test.beforeEach(async ({ page }) => {
    await setConfig(page);
    await mockJsonResponse(page, "**/api/v1/tenants/*/auth/token", TOKEN_RESPONSE);
  });

  test("a ?tenant= link signs in to that tenant with no field shown", async ({ page }) => {
    // What a mailed confirmation/reset link looks like when it lands here.
    await page.goto("/login?tenant=acme");

    await expect(page.getByTestId("resolved-tenant")).toHaveText("acme");
    await expect(page.getByLabel("Workspace")).toHaveCount(0);

    await page.getByLabel("Email").fill("alice@acme.com");
    await page.getByLabel("Password", { exact: true }).fill("Password123!");
    const reqPromise = page.waitForRequest(
      (r) => r.url().includes("/auth/token") && r.method() === "POST",
    );
    await page.getByRole("button", { name: /^sign in$/i }).click();

    expect((await reqPromise).url()).toContain("/api/v1/tenants/acme/auth/token");
  });

  test("\"Not your workspace?\" reveals the field", async ({ page }) => {
    await page.goto("/login?tenant=acme");
    await expect(page.getByLabel("Workspace")).toHaveCount(0);

    await page.getByRole("button", { name: /not your workspace/i }).click();

    await expect(page.getByLabel("Workspace")).toHaveValue("acme");
  });

  test("the tenant used last is remembered for the next visit", async ({ page }) => {
    await page.goto("/login");
    await page.getByLabel("Workspace").fill("globex");
    await page.getByLabel("Email").fill("dave@globex.com");
    await page.getByLabel("Password", { exact: true }).fill("Password123!");
    await page.getByRole("button", { name: /^sign in$/i }).click();

    // Only the identifier — never a token (ADR-0002).
    await expect
      .poll(() => page.evaluate(() => localStorage.getItem("boilerplate.dashboard.lastTenant")))
      .toBe("globex");
  });
});

test.describe("login — demo accounts", () => {
  test("the picker is absent unless demo mode is on", async ({ page }) => {
    await setConfig(page);
    await page.goto("/login");
    await expect(page.getByTestId("demo-accounts-open")).toHaveCount(0);
  });

  test("picking an account signs in with THAT account's tenant", async ({ page }) => {
    await setConfig(page, { demoMode: true, demoPassword: "DemoPass123!" });
    await mockJsonResponse(page, "**/api/v1/tenants/*/auth/token", TOKEN_RESPONSE);
    await page.goto("/login");

    await page.getByTestId("demo-accounts-open").click();
    // The dialog opens on the first tenant; pick the OTHER one, so the assertion below is
    // about the account's tenant travelling with it rather than a default holding.
    await page.getByRole("button", { name: /globex/i }).first().click();
    const reqPromise = page.waitForRequest(
      (r) => r.url().includes("/auth/token") && r.method() === "POST",
    );
    await page.getByRole("button", { name: /admin@globex\.com/i }).first().click();
    const req = await reqPromise;

    // The account carried its own tenant, so nobody had to choose one.
    expect(req.url()).toContain("/api/v1/tenants/globex/auth/token");
    expect(JSON.parse(req.postData() ?? "{}")).toMatchObject({ email: "admin@globex.com" });
  });

  test("with no demo password configured it prefills instead of signing in", async ({ page }) => {
    await setConfig(page, { demoMode: true, demoPassword: "" });
    let posted = 0;
    await page.route("**/api/v1/tenants/*/auth/token", async (route) => {
      posted += 1;
      await route.fulfill({ status: 200, body: JSON.stringify(TOKEN_RESPONSE) });
    });
    await page.goto("/login");

    await page.getByTestId("demo-accounts-open").click();
    await page.getByRole("button", { name: /admin@acme\.com/i }).first().click();

    await expect(page.getByLabel("Email")).toHaveValue("admin@acme.com");
    await expect(page.getByLabel("Password", { exact: true })).toHaveValue("");
    await expect(page.getByRole("status").first()).toContainText(/no demo password/i);
    expect(posted).toBe(0);
  });
});
