import { act } from "react";
import { createRoot, type Root } from "react-dom/client";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { toast } from "sonner";
import { markAllNotificationsRead, markNotificationRead } from "@/api/notifications";
import { ApiRequestError } from "@/lib/api-client";
import { useMarkNotificationsRead } from "@/components/notifications/use-mark-read";

vi.mock("sonner", () => ({ toast: { error: vi.fn() } }));
vi.mock("@/api/notifications", () => ({
  markAllNotificationsRead: vi.fn(),
  markNotificationRead: vi.fn(),
}));

const failure = new ApiRequestError(503, "Service Unavailable", {
  status: 503,
  title: "Service Unavailable",
  detail: "Try again in a moment.",
});

describe("useMarkNotificationsRead", () => {
  let container: HTMLDivElement;
  let root: Root;
  let latest: ReturnType<typeof useMarkNotificationsRead> | null = null;

  function Harness() {
    latest = useMarkNotificationsRead();
    return null;
  }

  beforeEach(async () => {
    vi.mocked(toast.error).mockClear();
    container = document.createElement("div");
    document.body.appendChild(container);
    const client = new QueryClient({ defaultOptions: { mutations: { retry: false } } });
    await act(async () => {
      root = createRoot(container);
      root.render(
        <QueryClientProvider client={client}>
          <Harness />
        </QueryClientProvider>,
      );
    });
  });

  afterEach(() => {
    act(() => root.unmount());
    container.remove();
  });

  it("says so when marking everything read fails", async () => {
    vi.mocked(markAllNotificationsRead).mockRejectedValue(failure);

    await act(async () => {
      await latest!.markAll.mutateAsync().catch(() => undefined);
    });

    expect(toast.error).toHaveBeenCalledWith("Could not mark notifications as read", {
      description: "Try again in a moment.",
    });
  });

  it("says so when marking one notification read fails", async () => {
    vi.mocked(markNotificationRead).mockRejectedValue(failure);

    await act(async () => {
      await latest!.markOne.mutateAsync("n-1").catch(() => undefined);
    });

    expect(toast.error).toHaveBeenCalledWith("Could not mark the notification as read", {
      description: "Try again in a moment.",
    });
  });
});
