import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from "vitest";
import {
  api,
  ApiRequestError,
  authPath,
  isTenantDeactivatedError,
  unwrap,
  unwrapVoid,
} from "@/lib/api-client";
import { loadRuntimeConfig } from "@/env";

function result<T>(init: { data?: T; error?: unknown; status: number }) {
  return {
    data: init.data,
    error: init.error,
    response: new Response(null, { status: init.status }),
  };
}

describe("authPath", () => {
  it("puts the tenant in the path — the one place a caller may name one", () => {
    expect(authPath("acme", "token")).toBe("/api/v1/tenants/acme/auth/token");
  });

  it("encodes a tenant id that is not URL-safe", () => {
    expect(authPath("a c/me", "refresh")).toBe("/api/v1/tenants/a%20c%2Fme/auth/refresh");
  });
});

describe("unwrap", () => {
  it("returns the body of a successful call", () => {
    expect(unwrap(result({ data: { id: "u1" }, status: 200 }))).toEqual({ id: "u1" });
  });

  it("returns undefined for a 204", () => {
    expect(unwrapVoid(result({ status: 204 }))).toBeUndefined();
  });

  it("throws an ApiRequestError carrying the problem details", () => {
    const problem = { status: 400, title: "Validation failed", detail: "Email is required" };
    try {
      unwrap(result({ error: problem, status: 400 }));
      expect.unreachable("unwrap must throw on a non-OK response");
    } catch (error) {
      expect(error).toBeInstanceOf(ApiRequestError);
      expect((error as ApiRequestError).status).toBe(400);
      expect((error as ApiRequestError).message).toBe("Validation failed");
      expect((error as ApiRequestError).problem).toEqual(problem);
    }
  });

  it("falls back to the status text when the body is not problem+json", () => {
    expect(() => unwrap(result({ status: 500 }))).toThrow(ApiRequestError);
  });
});

describe("single-flight refresh", () => {
  // jsdom's fetch is undici's: it refuses a relative URL, where a browser resolves it
  // against the document. The runtime apiBase stays "" (same origin) as in production —
  // this only gives openapi-fetch an origin to build the Request from.
  const SAME_ORIGIN = { baseUrl: "http://localhost" } as const;

  const json = (body: unknown, status = 200) =>
    new Response(JSON.stringify(body), {
      status,
      headers: { "Content-Type": "application/json" },
    });

  beforeAll(async () => {
    // env is a getter that throws until /config.json has been read; the client reads
    // apiBase on every call. apiBase "" keeps every request same-origin, as in prod.
    vi.stubGlobal(
      "fetch",
      vi.fn(async () => json({ apiBase: "", defaultTenant: "root" })),
    );
    await loadRuntimeConfig();
    vi.unstubAllGlobals();
  });

  beforeEach(() => {
    localStorage.setItem("boilerplate.console.accessToken", "expired-token");
    localStorage.setItem("boilerplate.console.tenant", "acme");
  });

  afterEach(() => {
    localStorage.clear();
    vi.unstubAllGlobals();
  });

  it("collapses two concurrent 401s into exactly one refresh", async () => {
    let refreshCalls = 0;
    let protectedCalls = 0;

    const fetchMock = vi.fn(async (input: Request | string) => {
      const url = typeof input === "string" ? input : input.url;

      if (url.includes("/auth/refresh")) {
        refreshCalls += 1;
        // A real refresh is a round trip; yielding a macrotask is what makes the two
        // callers genuinely concurrent rather than resolved in creation order.
        await new Promise((resolve) => setTimeout(resolve, 5));
        return json({ token: "fresh-token" });
      }

      protectedCalls += 1;
      // Both first attempts carry the expired token and are rejected; the retries
      // after the shared refresh succeed.
      return protectedCalls <= 2 ? json({ status: 401 }, 401) : json(["Permissions.Users.View"]);
    });
    vi.stubGlobal("fetch", fetchMock);

    const [first, second] = await Promise.all([
      api.GET("/api/v1/identity/permissions", SAME_ORIGIN),
      api.GET("/api/v1/identity/permissions", SAME_ORIGIN),
    ]);

    expect(refreshCalls).toBe(1);
    expect(protectedCalls).toBe(4);
    expect(first.response.status).toBe(200);
    expect(second.response.status).toBe(200);
    expect(localStorage.getItem("boilerplate.console.accessToken")).toBe("fresh-token");
  });

  it("frees the slot so a later 401 can refresh again", async () => {
    let refreshCalls = 0;
    let protectedCalls = 0;

    vi.stubGlobal(
      "fetch",
      vi.fn(async (input: Request | string) => {
        const url = typeof input === "string" ? input : input.url;
        if (url.includes("/auth/refresh")) {
          refreshCalls += 1;
          return json({ token: `fresh-${refreshCalls}` });
        }
        protectedCalls += 1;
        // Every odd attempt 401s, so each call needs its own refresh.
        return protectedCalls % 2 === 1 ? json({ status: 401 }, 401) : json([]);
      }),
    );

    await api.GET("/api/v1/identity/permissions", SAME_ORIGIN);
    await api.GET("/api/v1/identity/permissions", SAME_ORIGIN);

    expect(refreshCalls).toBe(2);
  });
});

describe("isTenantDeactivatedError", () => {
  it("recognises the deactivated-tenant 403 by its detail text", () => {
    const error = new ApiRequestError(403, "Forbidden", {
      status: 403,
      detail: "This tenant has been deactivated.",
    });
    expect(isTenantDeactivatedError(error)).toBe(true);
  });

  it("ignores any other 403", () => {
    const error = new ApiRequestError(403, "Forbidden", { status: 403, detail: "Missing permission" });
    expect(isTenantDeactivatedError(error)).toBe(false);
  });

  it("ignores non-API errors", () => {
    expect(isTenantDeactivatedError(new Error("boom"))).toBe(false);
  });
});
