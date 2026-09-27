import { beforeEach, describe, expect, it, vi } from "vitest";
import { screen } from "@testing-library/react";
import { listRoles, searchUsers, type UserDto } from "@/api/identity";
import type { Paged } from "@/lib/api-client";
import { ApiRequestError } from "@/lib/api-client";
import { pending, renderPage } from "@/test/render-page";
import { UsersPage } from "./users";

vi.mock("@/api/identity", () => ({
  searchUsers: vi.fn(),
  listRoles: vi.fn(),
  registerUser: vi.fn(),
}));

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

function page(items: UserDto[]): Paged<UserDto> {
  return {
    items,
    pageNumber: 1,
    pageSize: 20,
    totalCount: items.length,
    totalPages: items.length > 0 ? 1 : 0,
    hasPrevious: false,
    hasNext: false,
  };
}

describe("UsersPage", () => {
  beforeEach(() => {
    vi.mocked(listRoles).mockResolvedValue([]);
  });

  it("shows the loading skeleton while the first page is in flight", () => {
    vi.mocked(searchUsers).mockReturnValue(pending());

    renderPage(<UsersPage />);

    expect(screen.getByRole("status").getAttribute("aria-busy")).toBe("true");
    expect(screen.queryByText("No users yet")).toBeNull();
  });

  it("lists each user with a link to their detail page", async () => {
    vi.mocked(searchUsers).mockResolvedValue(page([ada]));

    renderPage(<UsersPage />);

    expect(await screen.findByText("1 user found")).toBeTruthy();
    const link = screen.getAllByRole("link", { name: /Ada Lovelace/ })[0];
    expect(link.getAttribute("href")).toBe("/identity/users/u-1");
    expect(screen.getByText("@ada")).toBeTruthy();
    expect(screen.queryByRole("status")).toBeNull();
  });

  it("invites registering the first member when the tenant has no users", async () => {
    vi.mocked(searchUsers).mockResolvedValue(page([]));

    renderPage(<UsersPage />);

    expect(await screen.findByText("No users yet")).toBeTruthy();
    // One in the header, one in the empty state.
    expect(screen.getAllByRole("button", { name: /Register user/ })).toHaveLength(2);
    expect(screen.queryByRole("alert")).toBeNull();
  });

  it("surfaces the server's problem detail when the search fails", async () => {
    vi.mocked(searchUsers).mockRejectedValue(
      new ApiRequestError(500, "Internal Server Error", {
        status: 500,
        detail: "The user directory is unavailable.",
      }),
    );

    renderPage(<UsersPage />);

    const alert = await screen.findByRole("alert");
    expect(alert.textContent).toBe("500 The user directory is unavailable.");
  });
});
