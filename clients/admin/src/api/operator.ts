import { apiFetch } from "@/lib/api-client";

/**
 * Operator token exchange — the only way to act inside another tenant (ADR-0002).
 *
 * The caller's own tenant comes from their signed token; the target travels in the body and is
 * checked server-side (root tenant + the root-only cross-tenant permission). What comes back is
 * access-only: no refresh token, no cookie, no session row, and a lifetime the server clamps to
 * its configured maximum. The admin app keeps it in memory only (see `acting-store`).
 */

export type ExchangeOperatorTokenInput = {
  targetTenantId: string;
  /** Omit to act as the tenant's own admin (resolved from the tenant record's AdminEmail). */
  targetUserId?: string;
  /** Required and audited — write it for the reviewer who reads the trail later. */
  reason: string;
  /** Requested lifetime; the server clamps rather than refusing. */
  durationMinutes?: number;
};

export type OperatorTokenExchangeResponse = {
  accessToken: string;
  accessTokenExpiresAt: string;
  targetTenantId: string;
  targetUserId: string;
  targetUserName?: string | null;
  actorUserId: string;
  actorTenantId: string;
  /** Grant row id — revoking it kills the token on its next request. */
  grantId: string;
  jti: string;
};

export type EndActingSessionResponse = {
  actorUserId: string;
  actorTenantId: string;
  impersonatedUserId: string;
  impersonatedTenantId: string;
  endedAtUtc: string;
};

/**
 * Always sent with the OPERATOR's own token: an exchanged token carries act_sub and the server
 * refuses to exchange again (no nesting). That matters for the impersonation picker, which runs
 * under an acting token but must exchange for the chosen user as the operator.
 */
export function exchangeOperatorToken(
  input: ExchangeOperatorTokenInput,
): Promise<OperatorTokenExchangeResponse> {
  return apiFetch<OperatorTokenExchangeResponse>(`/api/v1/identity/operator/token-exchange`, {
    method: "POST",
    asOperator: true,
    body: JSON.stringify({
      targetTenantId: input.targetTenantId,
      targetUserId: input.targetUserId ?? null,
      reason: input.reason,
      durationMinutes: input.durationMinutes ?? null,
    }),
  });
}

/**
 * Ends the acting session server-side: the grant is marked ended, so the exchanged token is
 * rejected on its next request. Returns no token — the operator never lost their own session,
 * so there is nothing to install. Sent with the acting token (act_sub is the gate).
 */
export function endActingSession(): Promise<EndActingSessionResponse> {
  return apiFetch<EndActingSessionResponse>(`/api/v1/identity/impersonation/end`, {
    method: "POST",
  });
}
