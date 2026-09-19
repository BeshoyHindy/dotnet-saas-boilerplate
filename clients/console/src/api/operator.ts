import { api, AS_OPERATOR, unwrap, type Schemas } from "@/lib/api-client";

export type ExchangeOperatorTokenCommand = Schemas["ExchangeOperatorTokenCommand"];
export type OperatorTokenExchangeResponse = Schemas["OperatorTokenExchangeResponse"];
export type ImpersonationResponse = Schemas["ImpersonationResponse"];
export type EndImpersonationResponse = Schemas["EndImpersonationResponse"];

/**
 * Acting as someone else — the two ways in, and the one way out (ADR-0002).
 *
 * Both entries mint a short-lived, access-only token: no refresh token, no cookie, no
 * session row, and a lifetime the server clamps to its configured maximum. The console
 * keeps that token in memory only (`acting-store`), never in localStorage.
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

/**
 * Cross a tenant boundary. Root only (`Permissions.Platform.Users.Impersonate` plus a
 * root-tenant check server-side); the caller's own tenant comes from their token and the
 * target travels in the body, never in a header.
 *
 * Always sent with the OPERATOR's own token: an acting token carries `act_sub` and the
 * server refuses to exchange again (no nesting). That matters for the impersonation
 * picker, which runs under an acting token but must exchange for the chosen user as the
 * operator.
 */
export async function exchangeOperatorToken(
  input: ExchangeOperatorTokenInput,
): Promise<OperatorTokenExchangeResponse> {
  return unwrap(
    await api.POST("/api/v1/identity/operator/token-exchange", {
      headers: AS_OPERATOR,
      body: {
        targetTenantId: input.targetTenantId,
        targetUserId: input.targetUserId ?? null,
        reason: input.reason,
        durationMinutes: input.durationMinutes ?? null,
      },
    }),
  );
}

export type StartImpersonationInput = {
  targetUserId: string;
  targetTenantId: string;
  reason?: string;
  /** 1..60 inclusive; null lets the server use its configured default. */
  durationMinutes?: number;
};

/**
 * Impersonate a user **in the caller's own tenant**. Since #9 a cross-tenant start is a
 * 403 that points at the exchange above, so this is the tenant-admin path only (the user
 * detail page's "Impersonate" action).
 *
 * Sent as the operator for the same reason the exchange is: no nesting.
 */
export async function startImpersonation(
  input: StartImpersonationInput,
): Promise<ImpersonationResponse> {
  return unwrap(
    await api.POST("/api/v1/identity/impersonation/start", {
      headers: AS_OPERATOR,
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
 * End the acting session server-side: the grant is marked ended, so the acting token is
 * rejected on its next request. Returns **no token** — the actor never lost their own
 * session, so there is nothing to install. Sent with the acting token (`act_sub` is the
 * gate), which is why it must not carry the operator override.
 */
export async function endActingSession(): Promise<EndImpersonationResponse> {
  return unwrap(await api.POST("/api/v1/identity/impersonation/end", {}));
}
