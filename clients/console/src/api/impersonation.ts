import { api, unwrap, type Schemas } from "@/lib/api-client";

export type ImpersonationResponse = Schemas["ImpersonationResponse"];
export type EndImpersonationResponse = Schemas["EndImpersonationResponse"];

export type StartImpersonationInput = {
  targetUserId: string;
  targetTenantId: string;
  reason?: string;
  /** 1..60 inclusive; null lets the server use its configured default. */
  durationMinutes?: number;
};

/**
 * Issues a short-lived impersonation access token representing the target user, so an
 * operator can enter that tenant. The caller's own tenant comes from their token
 * (ADR-0002) and is what the server checks: root operators may impersonate any tenant,
 * tenant admins only their own. The target tenant travels in the body, never in a header.
 *
 * The console installs the token in place (there is only one client app now), stashing
 * the operator's own session so it can be restored — see `token-store.ts`.
 */
export async function startImpersonation(
  input: StartImpersonationInput,
): Promise<ImpersonationResponse> {
  return unwrap(
    await api.POST("/api/v1/identity/impersonation/start", {
      body: {
        targetUserId: input.targetUserId,
        targetTenantId: input.targetTenantId,
        reason: input.reason ?? null,
        durationMinutes: input.durationMinutes ?? null,
      },
    }),
  );
}

/**
 * Ends the impersonation the current token represents. The response is access-only: it
 * runs inside the impersonated tenant's context and cannot mint a session row in the
 * operator's tenant, so the console restores the operator's stashed session instead
 * (the operator's own session was never revoked).
 */
export async function endImpersonation(): Promise<EndImpersonationResponse> {
  return unwrap(await api.POST("/api/v1/identity/impersonation/end", {}));
}
