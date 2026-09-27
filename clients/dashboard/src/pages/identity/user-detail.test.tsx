import { beforeEach, describe, expect, it, vi } from "vitest";
import { screen } from "@testing-library/react";
import { getUserById, getUserRoles, type UserDto } from "@/api/identity";
import { useAuth } from "@/auth/use-auth";
import { ApiRequestError } from "@/lib/api-client";
import { pending, renderPage } from "@/test/render-page";
import { UserDetailPage } from "./user-detail";

vi.mock("@/api/identity", () => ({
  getUserById: vi.fn(),
  getUserRoles: vi.fn(),
  getUserSessionsAdmin: vi.fn(),
  adminRevokeAllUserSessions: vi.fn(),
  adminRevokeUserSession: vi.fn(),
  assignUserRoles: vi.fn(),
  confirmUserEmail: vi.fn(),
  deleteUser: vi.fn(),
  resendUserConfirmationEmail: vi.fn(),
  toggleUserStatus: vi.fn(),
}));
vi.mock("@/auth/use-auth", () => ({ useAuth: vi.fn() }));

const ada: UserDto = {
  id: "u-1",
  userName: "ada",
  firstName: "Ada",
  lastName: "Lovelace",
  email: "ada@example.com",
  phoneNumber: null,
  imageUrl: null,
  isActive: true,
  emailConfirmed: true,
  twoFactorEnabled: false,
};

const route = { path: "/identity/users/:userId", url: "/identity/users/u-1" };

describe("UserDetailPage", () => {
  beforeEach(() => {
    // An admin who can see the user but not their sessions: the sessions query stays off.
    vi.mocked(useAuth).mockReturnValue({
      user: { id: "admin", permissions: [] },
    } as unknown as ReturnType<typeof useAuth>);
  });

  it("holds skeletons while the user loads", () => {
    vi.mocked(getUserById).mockReturnValue(pending());
    vi.mocked(getUserRoles).mockReturnValue(pending());

    const { container } = renderPage(<UserDetailPage />, route);

    expect(screen.getByRole("link", { name: /Back to users/ })).toBeTruthy();
    expect(container.querySelectorAll(".skeleton").length).toBeGreaterThan(0);
    expect(screen.queryByText("Ada Lovelace")).toBeNull();
  });

  it("shows the user and the roles they hold", async () => {
    vi.mocked(getUserById).mockResolvedValue(ada);
    vi.mocked(getUserRoles).mockResolvedValue([
      { roleId: "r-1", roleName: "Admin", description: "Everything", enabled: true },
      { roleId: "r-2", roleName: "Basic", description: null, enabled: false },
    ]);

    renderPage(<UserDetailPage />, route);

    expect(await screen.findByRole("heading", { name: "Ada Lovelace" })).toBeTruthy();
    expect(await screen.findByText("Admin")).toBeTruthy();
    expect(screen.getByText("Basic")).toBeTruthy();
    expect(getUserById).toHaveBeenCalledWith("u-1");
  });

  it("points to the roles page when no roles are defined", async () => {
    vi.mocked(getUserById).mockResolvedValue(ada);
    vi.mocked(getUserRoles).mockResolvedValue([]);

    renderPage(<UserDetailPage />, route);

    expect(await screen.findByText(/No roles defined\./)).toBeTruthy();
    expect(screen.getByRole("link", { name: "Create one" }).getAttribute("href")).toBe(
      "/identity/roles",
    );
  });

  it("explains a failed load instead of rendering the user", async () => {
    vi.mocked(getUserById).mockRejectedValue(
      new ApiRequestError(404, "Not Found", { status: 404, detail: "User Not Found." }),
    );
    vi.mocked(getUserRoles).mockResolvedValue([]);

    renderPage(<UserDetailPage />, route);

    expect(await screen.findByText(/404 User Not Found\./)).toBeTruthy();
    expect(screen.getByText(/Failure/)).toBeTruthy();
    expect(screen.queryByRole("heading", { name: "Ada Lovelace" })).toBeNull();
  });
});
