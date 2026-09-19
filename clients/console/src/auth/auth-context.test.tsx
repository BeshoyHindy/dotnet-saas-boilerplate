import { act } from "react";
import { createRoot, type Root } from "react-dom/client";
import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from "vitest";
import { QueryClientProvider } from "@tanstack/react-query";
import { queryClient } from "@/lib/query-client";
import { AuthProvider } from "@/auth/auth-context";
import { useAuth } from "@/auth/use-auth";
import { actingStore, type ActingSession } from "@/auth/acting-store";
import { loadRuntimeConfig } from "@/env";
import { issueToken, type TokenResponse } from "@/auth/api";

// `login()` goes through `issueToken` (openapi-fetch), which needs a `baseUrl` per
// call to build an absolute URL under jsdom's undici-backed `fetch` — the real
// client never passes one (a browser resolves a relative URL against the document).
// Mocking `@/auth/api` here is the same seam `api-client.test.ts` avoids only because
// it can pass `{ baseUrl }` straight into `api.GET`; `login()` doesn't expose that.
// The boot refresh path below calls the raw global `fetch` directly (no URL
// construction to work around), so it's stubbed instead, same as `api-client.test.ts`.
vi.mock("@/auth/api", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@/auth/api")>();
  return { ...actual, issueToken: vi.fn() };
});
vi.mock("@/api/identity", () => ({ getMyPermissions: vi.fn(async () => []) }));

/** Minimal JWT-shaped string the console's decoder can read (see tests/helpers/auth-seed.ts). */
function fakeJwt(payload: Record<string, unknown>): string {
  const b64url = (obj: unknown) =>
    btoa(JSON.stringify(obj)).replace(/=+$/, "").replace(/\+/g, "-").replace(/\//g, "_");
  return [b64url({ alg: "HS256", typ: "JWT" }), b64url(payload), "sig"].join(".");
}

const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });

const ACTING: ActingSession = {
  accessToken: "acting-token",
  tenantId: "acme",
  tenantName: "Acme Corp",
  userId: "u-9",
  expiresAt: new Date(Date.now() + 900_000).toISOString(),
  jti: "acting-jti",
};

function Harness({ onReady }: { onReady: (ctx: ReturnType<typeof useAuth>) => void }) {
  const ctx = useAuth();
  onReady(ctx);
  return null;
}

describe("AuthProvider session-ending paths", () => {
  beforeAll(async () => {
    // env is a getter that throws until /config.json has been read (see api-client.test.ts).
    vi.stubGlobal(
      "fetch",
      vi.fn(async () => json({ apiBase: "", defaultTenant: "root" })),
    );
    await loadRuntimeConfig();
    vi.unstubAllGlobals();
  });

  let container: HTMLDivElement;
  let root: Root;
  let latest: ReturnType<typeof useAuth> | null = null;

  beforeEach(() => {
    localStorage.clear();
    actingStore.clear();
    queryClient.clear();
    container = document.createElement("div");
    document.body.appendChild(container);
    latest = null;
  });

  afterEach(() => {
    act(() => root.unmount());
    container.remove();
    localStorage.clear();
    actingStore.clear();
    queryClient.clear();
    vi.unstubAllGlobals();
  });

  async function mount() {
    await act(async () => {
      root = createRoot(container);
      root.render(
        <QueryClientProvider client={queryClient}>
          <AuthProvider>
            <Harness onReady={(ctx) => (latest = ctx)} />
          </AuthProvider>
        </QueryClientProvider>,
      );
    });
  }

  it("login() drops any acting session", async () => {
    actingStore.start(ACTING);
    expect(actingStore.get()).not.toBeNull();

    const accessToken = fakeJwt({
      sub: "u-1",
      tenant: "acme",
      exp: Math.floor(Date.now() / 1000) + 3600,
    });
    vi.mocked(issueToken).mockResolvedValue({ accessToken } as TokenResponse);

    await mount();
    await act(async () => {
      await latest!.login({ email: "a@acme.test", password: "x", tenant: "acme" });
    });

    expect(actingStore.get()).toBeNull();
  });

  it("a failed refresh clears the acting session", async () => {
    actingStore.start(ACTING);
    localStorage.setItem("boilerplate.console.accessToken", "expired-token");
    localStorage.setItem("boilerplate.console.tenant", "acme");

    vi.stubGlobal(
      "fetch",
      vi.fn(async (input: Request | string) => {
        const url = typeof input === "string" ? input : input.url;
        if (url.includes("/auth/refresh")) return json({ status: 401 }, 401);
        return json([]);
      }),
    );

    // Boot's silent refresh runs because the stored token is expired but a tenant
    // is remembered (see auth-context's isInitializing).
    await mount();
    await vi.waitFor(() => {
      expect(latest?.isInitializing).toBe(false);
    });

    expect(actingStore.get()).toBeNull();
    expect(localStorage.getItem("boilerplate.console.accessToken")).toBeNull();
  });
});
