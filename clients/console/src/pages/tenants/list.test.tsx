import { beforeEach, describe, expect, it, vi } from "vitest";
import { screen } from "@testing-library/react";
import { listTenants, type TenantDto } from "@/api/tenants";
import { useAuth } from "@/auth/use-auth";
import { ApiRequestError, type Paged } from "@/lib/api-client";
import { MultitenancyPermissions } from "@/lib/permissions";
import { pending, renderPage } from "@/test/render-page";
import { TenantsListPage } from "./list";

vi.mock("@/api/tenants", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/api/tenants")>()),
  listTenants: vi.fn(),
}));
vi.mock("@/auth/use-auth", () => ({ useAuth: vi.fn() }));

const acme: TenantDto = {
  id: "acme",
  name: "Acme Corp",
  adminEmail: "admin@acme.example",
  isActive: true,
  issuer: null,
  validUpto: "2027-01-01T00:00:00Z",
};

function page(items: TenantDto[]): Paged<TenantDto> {
  return {
    items,
    pageNumber: 1,
    pageSize: 12,
    totalCount: items.length,
    totalPages: items.length > 0 ? 1 : 0,
    hasPrevious: false,
    hasNext: false,
  };
}

function signInWith(permissions: string[]) {
  vi.mocked(useAuth).mockReturnValue({
    user: { id: "operator", permissions },
    acting: null,
  } as unknown as ReturnType<typeof useAuth>);
}

describe("TenantsListPage", () => {
  beforeEach(() => signInWith([MultitenancyPermissions.Tenants.Create]));

  it("says it is loading while the registry is in flight", () => {
    vi.mocked(listTenants).mockReturnValue(pending());

    renderPage(<TenantsListPage />);

    expect(screen.getByRole("status").textContent).toBe("Loading…");
    expect(screen.getByText("Loading the registry…")).toBeTruthy();
  });

  it("lists every tenant with its admin and status", async () => {
    vi.mocked(listTenants).mockResolvedValue(page([acme]));

    renderPage(<TenantsListPage />);

    expect(await screen.findByText("1 tenant registered")).toBeTruthy();
    expect(screen.getAllByText("Acme Corp").length).toBeGreaterThan(0);
    expect(screen.getAllByText("admin@acme.example").length).toBeGreaterThan(0);
    expect(screen.getAllByText("Active").length).toBeGreaterThan(0);
    expect(screen.queryByRole("status")).toBeNull();
  });

  it("offers to provision the first tenant when the registry is empty", async () => {
    vi.mocked(listTenants).mockResolvedValue(page([]));

    renderPage(<TenantsListPage />);

    expect(await screen.findByText("No tenants yet.")).toBeTruthy();
    expect(screen.getByRole("button", { name: /New tenant/ })).toBeTruthy();
  });

  it("hides New tenant from an operator without the create permission", async () => {
    signInWith([]);
    vi.mocked(listTenants).mockResolvedValue(page([]));

    renderPage(<TenantsListPage />);

    expect(await screen.findByText("No tenants yet.")).toBeTruthy();
    expect(screen.queryByRole("button", { name: /New tenant/ })).toBeNull();
  });

  it("shows the server's problem detail, not an empty registry, when the list fails", async () => {
    vi.mocked(listTenants).mockRejectedValue(
      new ApiRequestError(503, "Service Unavailable", {
        status: 503,
        detail: "The tenant store is unavailable.",
      }),
    );

    renderPage(<TenantsListPage />);

    expect(await screen.findByText(/The tenant store is unavailable\./)).toBeTruthy();
    expect(screen.queryByText("No tenants yet.")).toBeNull();
  });
});
