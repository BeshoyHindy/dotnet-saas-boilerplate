import { apiFetch, authPath } from "@/lib/api-client";

export type TokenResponse = {
  accessToken: string;
  refreshToken: string;
  accessTokenExpiresAt: string;
  refreshTokenExpiresAt: string;
};

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
    // X-Client-App marks this client as the platform-admin app. Used by the
    // API to enforce the SuperAdmin / dashboard boundary.
    headers: { "X-Client-App": "admin" },
    skipAuth: true,
  });
}
