import { beforeEach, describe, expect, it, vi } from "vitest";
import { listNotifications } from "@/api/notifications";

// The real client builds a Request from a relative URL, which undici (jsdom's fetch) refuses; the
// question here is what `listNotifications` puts on the wire, so the transport is stubbed out.
const { get } = vi.hoisted(() => ({ get: vi.fn() }));
vi.mock("@/lib/api-client", () => ({
  api: { GET: get },
  unwrap: (r: { data: unknown }) => r.data,
  unwrapVoid: () => undefined,
}));

const page = {
  items: [],
  pageNumber: 2,
  pageSize: 30,
  totalCount: 0,
  totalPages: 0,
  hasNext: false,
  hasPrevious: true,
};

describe("listNotifications", () => {
  beforeEach(() => {
    get.mockReset();
    get.mockResolvedValue({ data: page });
  });

  it("sends the shared paging keys the endpoint binds", async () => {
    await listNotifications({ unreadOnly: true, pageNumber: 2, pageSize: 30 });

    expect(get).toHaveBeenCalledWith("/api/v1/notifications", {
      params: { query: { UnreadOnly: true, PageNumber: 2, PageSize: 30 } },
    });
  });

  it("leaves paging to the server's defaults when none is asked for", async () => {
    await listNotifications();

    expect(get).toHaveBeenCalledWith("/api/v1/notifications", {
      params: { query: { UnreadOnly: undefined, PageNumber: undefined, PageSize: undefined } },
    });
  });

  it("returns the paged response as-is", async () => {
    await expect(listNotifications()).resolves.toEqual(page);
  });
});
