import { beforeEach, describe, expect, it, vi } from "vitest";
import { screen } from "@testing-library/react";
import {
  getTenantProvisioningStatus,
  getTenantStatus,
  type TenantProvisioningStatus,
  type TenantStatusDto,
} from "@/api/tenants";
import { useAuth } from "@/auth/use-auth";
import { ApiRequestError } from "@/lib/api-client";
import { pending, renderPage } from "@/test/render-page";
import { TenantDetailPage } from "./detail";

vi.mock("@/api/tenants", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/api/tenants")>()),
  getTenantStatus: vi.fn(),
  getTenantProvisioningStatus: vi.fn(),
}));
vi.mock("@/api/impersonation-grants", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/api/impersonation-grants")>()),
  listImpersonationGrants: vi.fn(),
}));
vi.mock("@/auth/use-auth", () => ({ useAuth: vi.fn() }));

const acme: TenantStatusDto = {
  id: "acme",
  name: "Acme Corp",
  adminEmail: "admin@acme.example",
  isActive: true,
  issuer: null,
  validUpto: "2027-01-01T00:00:00Z",
  expiryState: "Active",
  graceEndsUtc: "2027-01-08T00:00:00Z",
};

const completedRun: TenantProvisioningStatus = {
  tenantId: "acme",
  correlationId: "c-1",
  status: "Completed",
  currentStep: null,
  error: null,
  createdUtc: "2026-01-01T00:00:00Z",
  startedUtc: "2026-01-01T00:00:01Z",
  completedUtc: "2026-01-01T00:00:05Z",
  steps: [
    {
      step: "Database",
      status: "Completed",
      startedUtc: "2026-01-01T00:00:01Z",
      completedUtc: "2026-01-01T00:00:03Z",
      error: null,
    },
  ],
};

const route = { path: "/tenants/:id", url: "/tenants/acme" };

describe("TenantDetailPage", () => {
  beforeEach(() => {
    // An operator with no extra permissions: the grants and branding cards stay idle.
    vi.mocked(useAuth).mockReturnValue({
      user: { id: "operator", permissions: [] },
      acting: null,
    } as unknown as ReturnType<typeof useAuth>);
  });

  it("says it is loading while the tenant is in flight", () => {
    vi.mocked(getTenantStatus).mockReturnValue(pending());
    vi.mocked(getTenantProvisioningStatus).mockReturnValue(pending());

    renderPage(<TenantDetailPage />, route);

    expect(screen.getByRole("status").textContent).toContain("Loading tenant");
    expect(screen.queryByText("Overview")).toBeNull();
  });

  it("shows the tenant and its provisioning run", async () => {
    vi.mocked(getTenantStatus).mockResolvedValue(acme);
    vi.mocked(getTenantProvisioningStatus).mockResolvedValue(completedRun);

    renderPage(<TenantDetailPage />, route);

    expect(await screen.findByRole("heading", { name: "Acme Corp", level: 2 })).toBeTruthy();
    expect(screen.getByText("acme", { selector: "code" })).toBeTruthy();
    expect(await screen.findByText("Database")).toBeTruthy();
    expect(getTenantStatus).toHaveBeenCalledWith("acme");
  });

  it("explains a tenant the provisioning pipeline never ran for", async () => {
    vi.mocked(getTenantStatus).mockResolvedValue(acme);
    vi.mocked(getTenantProvisioningStatus).mockRejectedValue(
      new ApiRequestError(404, "Not Found", { status: 404 }),
    );

    renderPage(<TenantDetailPage />, route);

    expect(
      await screen.findByText(/wasn't created through the provisioning pipeline/),
    ).toBeTruthy();
    expect(screen.getByText("Not tracked")).toBeTruthy();
    expect(screen.queryByText(/Failure/)).toBeNull();
  });

  it("shows the server's problem detail when the tenant cannot be loaded", async () => {
    vi.mocked(getTenantStatus).mockRejectedValue(
      new ApiRequestError(404, "Not Found", { status: 404, detail: "Tenant acme not found." }),
    );
    vi.mocked(getTenantProvisioningStatus).mockReturnValue(pending());

    renderPage(<TenantDetailPage />, route);

    expect(await screen.findByText(/Tenant acme not found\./)).toBeTruthy();
    expect(screen.queryByText("Overview")).toBeNull();
  });
});
