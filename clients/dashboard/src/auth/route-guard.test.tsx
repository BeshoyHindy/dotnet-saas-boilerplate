import { describe, expect, it, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { useAuth } from "@/auth/use-auth";
import { RouteGuard } from "@/auth/route-guard";

vi.mock("@/auth/use-auth", () => ({ useAuth: vi.fn() }));

function withPermissions(permissions: readonly string[]) {
  vi.mocked(useAuth).mockReturnValue({
    user: { permissions },
    permissionsHydrated: true,
  } as unknown as ReturnType<typeof useAuth>);
}

function renderGuard(props: { perms?: readonly string[]; anyPerms?: readonly string[] }) {
  return render(
    <MemoryRouter>
      <RouteGuard {...props}>
        <div>secret</div>
      </RouteGuard>
    </MemoryRouter>,
  );
}

describe("RouteGuard anyPerms", () => {
  it("renders when the user holds at least one of anyPerms", () => {
    withPermissions(["Permissions.Files.ViewTrash"]);
    renderGuard({ anyPerms: ["Permissions.Files.ViewTrash", "Permissions.Other.ViewTrash"] });
    expect(screen.getByText("secret")).toBeTruthy();
  });

  it("forbids when the user holds none of anyPerms", () => {
    withPermissions([]);
    renderGuard({ anyPerms: ["Permissions.Files.ViewTrash", "Permissions.Other.ViewTrash"] });
    expect(screen.queryByText("secret")).toBeNull();
  });

  it("combines perms and anyPerms via AND — perms alone isn't enough", () => {
    // Holds the `perms` requirement but none of `anyPerms` — same semantics as
    // nav-data.ts's isNavItemVisible, which ANDs the two gates together.
    withPermissions(["Permissions.Files.Update"]);
    renderGuard({
      perms: ["Permissions.Files.Update"],
      anyPerms: ["Permissions.Files.ViewTrash", "Permissions.Other.ViewTrash"],
    });
    expect(screen.queryByText("secret")).toBeNull();
  });
});
