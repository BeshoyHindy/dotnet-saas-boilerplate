import { act } from "react";
import { createRoot, type Root } from "react-dom/client";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { MemoryRouter } from "react-router-dom";
import type { AuthContextValue } from "@/auth/auth-context";
import { useAuth } from "@/auth/use-auth";
import { ActingBanner } from "@/components/layout/acting-banner";

vi.mock("@/auth/use-auth", () => ({ useAuth: vi.fn() }));

const REASON = "Your session inside Acme Corp ended (revoked or expired). You are back in your own account.";

function authWith(overrides: Partial<AuthContextValue>): AuthContextValue {
  return {
    user: { id: "op-1", tenant: "root", permissions: [] },
    isAuthenticated: true,
    isInitializing: false,
    permissionsHydrated: true,
    login: vi.fn(),
    logout: vi.fn(),
    refreshPermissions: vi.fn(),
    acting: null,
    enterTenant: vi.fn(),
    impersonateInOwnTenant: vi.fn(),
    exitTenant: vi.fn(),
    actingEndedNotice: null,
    dismissActingEndedNotice: vi.fn(),
    ...overrides,
  };
}

describe("ActingBanner", () => {
  let container: HTMLDivElement;
  let root: Root;

  beforeEach(() => {
    container = document.createElement("div");
    document.body.appendChild(container);
  });

  afterEach(() => {
    act(() => root.unmount());
    container.remove();
  });

  function render() {
    act(() => {
      root = createRoot(container);
      root.render(
        <QueryClientProvider client={new QueryClient()}>
          <MemoryRouter>
            <ActingBanner />
          </MemoryRouter>
        </QueryClientProvider>,
      );
    });
  }

  it("stays up with the reason after an involuntary drop, until dismissed", () => {
    const dismiss = vi.fn();
    vi.mocked(useAuth).mockReturnValue(
      authWith({ actingEndedNotice: REASON, dismissActingEndedNotice: dismiss }),
    );

    render();

    const banner = container.querySelector('[data-testid="acting-ended-banner"]');
    expect(banner?.getAttribute("role")).toBe("status");
    expect(banner?.textContent).toContain(REASON);

    act(() => {
      container.querySelector<HTMLButtonElement>('[data-testid="acting-ended-dismiss"]')!.click();
    });
    expect(dismiss).toHaveBeenCalledOnce();
  });

  it("renders nothing when not acting and nothing ended", () => {
    vi.mocked(useAuth).mockReturnValue(authWith({}));

    render();

    expect(container.innerHTML).toBe("");
  });
});
