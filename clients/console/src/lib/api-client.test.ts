import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from "vitest";
import {
  api,
  ApiRequestError,
  AS_OPERATOR,
  AS_OPERATOR_HEADER,
  authPath,
  isTenantDeactivatedError,
  unwrap,
  unwrapVoid,
} from "@/lib/api-client";
import { loadRuntimeConfig } from "@/env";
import { actingStore, type ActingSession } from "@/auth/acting-store";

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

describe("the acting layer", () => {
  const SAME_ORIGIN = { baseUrl: "http://localhost" } as const;

  const json = (body: unknown, status = 200) =>
    new Response(JSON.stringify(body), {
      status,
      headers: { "Content-Type": "application/json" },
    });

  const ACTING: ActingSession = {
    accessToken: "acting-token",
    tenantId: "acme",
    tenantName: "Acme Corp",
    userId: "u-9",
    expiresAt: new Date(Date.now() + 900_000).toISOString(),
  };

  beforeEach(() => {
    localStorage.setItem("boilerplate.console.accessToken", "operator-token");
    localStorage.setItem("boilerplate.console.tenant", "root");
  });

  afterEach(() => {
    actingStore.clear();
    localStorage.clear();
    vi.unstubAllGlobals();
  });

  /** The Authorization header of every request the stub saw. */
  function recordBearers() {
    const bearers: string[] = [];
    const seenFlag: boolean[] = [];
    vi.stubGlobal(
      "fetch",
      vi.fn(async (input: Request | string) => {
        const request = input as Request;
        bearers.push(request.headers.get("Authorization") ?? "");
        seenFlag.push(request.headers.get(AS_OPERATOR_HEADER) !== null);
        return json([]);
      }),
    );
    return { bearers, seenFlag };
  }

  it("sends the acting token while acting", async () => {
    actingStore.start(ACTING);
    const { bearers } = recordBearers();

    await api.GET("/api/v1/identity/permissions", SAME_ORIGIN);

    expect(bearers).toEqual(["Bearer acting-token"]);
  });

  it("sends the operator's own token when the call opts out, and never leaks the flag", async () => {
    actingStore.start(ACTING);
    const { bearers, seenFlag } = recordBearers();

    await api.GET("/api/v1/identity/permissions", { ...SAME_ORIGIN, headers: AS_OPERATOR });

    expect(bearers).toEqual(["Bearer operator-token"]);
    // The sentinel is a transport detail; it must never reach the wire.
    expect(seenFlag).toEqual([false]);
  });

  it("drops the acting session on a 401 instead of spending the refresh cookie", async () => {
    actingStore.start(ACTING);
    let refreshCalls = 0;
    vi.stubGlobal(
      "fetch",
      vi.fn(async (input: Request | string) => {
        const url = typeof input === "string" ? input : input.url;
        if (url.includes("/auth/refresh")) {
          refreshCalls += 1;
          return json({ token: "should-not-happen" });
        }
        return json({ status: 401 }, 401);
      }),
    );

    const result = await api.GET("/api/v1/identity/permissions", SAME_ORIGIN);

    // The acting token is access-only: there is nothing to renew, so no refresh is tried.
    expect(refreshCalls).toBe(0);
    expect(result.response.status).toBe(401);
    expect(actingStore.get()).toBeNull();
    expect(actingStore.getNotice()).toContain("Acme Corp");
    // The operator's own session is untouched.
    expect(localStorage.getItem("boilerplate.console.accessToken")).toBe("operator-token");
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
