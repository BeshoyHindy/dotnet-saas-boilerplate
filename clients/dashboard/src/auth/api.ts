import { apiFetch, authPath } from "@/lib/api-client";

export type TokenResponse = {
  accessToken: string;
  refreshToken: string;
  accessTokenExpiresAt: string;
  refreshTokenExpiresAt: string;
};

/**
 * Ends the session server-side. Clearing localStorage is not enough: the refresh token also lives
 * in an HttpOnly cookie this code cannot touch, and `/auth/refresh` accepts that cookie on its own,
 * so a purely local sign-out would leave a full token pair recoverable at the browser. The server
 * revokes the session row and sends the Set-Cookie that clears the cookie.
 *
 * `skipAuth` with the header set by hand: the endpoint is anonymous so that a caller whose access
 * token has already expired can still sign out, but we pass the token when we have one because its
 * `sid` claim names the session directly.
 *
 * Best-effort by design — the caller clears local state regardless, so a network failure still
 * signs the user out of this tab.
 */
export function revokeSession(input: {
  tenant: string;
  accessToken: string | null;
  refreshToken: string | null;
}) {
  return apiFetch<void>(authPath(input.tenant, "logout"), {
    method: "POST",
    body: JSON.stringify({ refreshToken: input.refreshToken }),
    headers: input.accessToken ? { Authorization: `Bearer ${input.accessToken}` } : undefined,
    skipAuth: true,
  });
}

export function issueToken(input: {
  email: string;
  password: string;
  tenant: string;
}) {
  // The tenant is part of the login URL, not a header: it is the one place the
  // caller may name a tenant, and only while no token exists yet (ADR-0002).
  return apiFetch<TokenResponse>(authPath(input.tenant, "token"), {
    method: "POST",
    body: JSON.stringify({ email: input.email, password: input.password }),
    // X-Client-App tells the API this credential request originated from the
    // tenant dashboard. The server uses it to enforce the SuperAdmin / app
    // boundary — a root-tenant login submitted with X-Client-App=dashboard is
    // rejected with 403 instead of receiving a usable token.
    headers: { "X-Client-App": "dashboard" },
    skipAuth: true,
  });
}
