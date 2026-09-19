import { api, unwrap, unwrapVoid, type Schemas } from "@/lib/api-client";

export type TokenResponse = Schemas["TokenResponse"];

/**
 * Sign in. The tenant is part of the login URL, not a header: it is the one place the
 * caller may name a tenant, and only while no token exists yet (ADR-0002).
 *
 * The response body carries a refresh token for non-browser clients; the dashboard ignores
 * it and relies on the `HttpOnly; SameSite=Strict` cookie the same response sets, which
 * works because the dashboard is served from the API's origin (nginx proxies `/api`).
 *
 * No `X-Client-App` header: that header existed to keep root operators out of the tenant
 * app. The boundary is real again (ADR-0008) but it is not a header's to draw — an
 * operator who signs in here gets exactly the tenant session their own token describes,
 * and platform screens live in the other app, behind the server's own permission checks.
 */
export async function issueToken(input: {
  email: string;
  password: string;
  tenant: string;
}): Promise<TokenResponse> {
  return unwrap(
    await api.POST("/api/v1/tenants/{tenant}/auth/token", {
      params: { path: { tenant: input.tenant } },
      body: { email: input.email, password: input.password },
    }),
  );
}

/**
 * End the session server-side. The refresh cookie is `HttpOnly`, so only the server can
 * clear it — and `/auth/refresh` accepts that cookie alone, which is why signing out has
 * to be a round trip rather than a `localStorage.clear()`. The endpoint is anonymous by
 * design: the access token is usually already gone by the time a browser signs out.
 */
export async function endSession(tenant: string): Promise<void> {
  unwrapVoid(
    await api.POST("/api/v1/tenants/{tenant}/auth/logout", {
      params: { path: { tenant } },
      // The refresh token rides in the HttpOnly cookie the browser attaches; the body
      // field exists for non-browser clients that hold it themselves.
      body: { refreshToken: null },
    }),
  );
}
