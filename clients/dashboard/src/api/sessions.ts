import { api, unwrap, unwrapVoid, type Paged, type Schemas } from "@/lib/api-client";

export type UserSessionDto = Schemas["UserSessionDto"];

// ─── Self-service ────────────────────────────────────────────────────

export async function getMySessions(): Promise<UserSessionDto[]> {
  return unwrap(await api.GET("/api/v1/identity/sessions/me", {}));
}

export async function revokeSession(sessionId: string): Promise<void> {
  unwrapVoid(
    await api.DELETE("/api/v1/identity/sessions/{sessionId}", {
      params: { path: { sessionId } },
    }),
  );
}

export async function revokeAllOtherSessions(): Promise<Schemas["RevokeSessionsResponse"]> {
  return unwrap(
    await api.POST("/api/v1/identity/sessions/revoke-all", {
      // null ⇒ the server keeps the caller's current session and revokes the rest.
      body: { exceptSessionId: null },
    }),
  );
}

// ─── Admin / tenant-wide ─────────────────────────────────────────────

export type TenantSessionsParams = {
  includeInactive?: boolean;
  search?: string;
  pageNumber?: number;
  pageSize?: number;
};

export async function getTenantSessions(
  params: TenantSessionsParams = {},
): Promise<Paged<UserSessionDto>> {
  return unwrap(
    await api.GET("/api/v1/identity/sessions", {
      params: {
        query: {
          includeInactive: params.includeInactive,
          search: params.search,
          pageNumber: params.pageNumber ?? 1,
          pageSize: params.pageSize ?? 50,
        },
      },
    }),
  );
}

/**
 * Admin: revoke a session belonging to a specific user. Maps to
 * DELETE /users/{userId}/sessions/{sessionId}.
 */
export async function adminRevokeUserSessionById(userId: string, sessionId: string): Promise<void> {
  unwrapVoid(
    await api.DELETE("/api/v1/identity/users/{userId}/sessions/{sessionId}", {
      params: { path: { userId, sessionId } },
    }),
  );
}

export async function adminRevokeAllUserSessions(
  userId: string,
): Promise<Schemas["RevokeSessionsResponse"]> {
  return unwrap(
    await api.POST("/api/v1/identity/users/{userId}/sessions/revoke-all", {
      params: { path: { userId } },
      body: { userId, reason: null },
    }),
  );
}
