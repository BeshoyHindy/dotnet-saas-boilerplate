import { createContext, useCallback, useEffect, useMemo, useRef, useState, useSyncExternalStore, type ReactNode } from "react";
import { useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { tokenStore } from "@/auth/token-store";
import { actingStore, type ActingSession } from "@/auth/acting-store";
import { decodeJwt, isTokenExpired, type JwtClaims } from "@/auth/jwt";
import { issueToken, revokeSession } from "@/auth/api";
import { refreshAccessToken } from "@/lib/api-client";
import { getMyPermissions } from "@/api/users";
import { endActingSession, exchangeOperatorToken } from "@/api/operator";

export type AuthUser = {
  id: string;
  email?: string;
  name?: string;
  tenant?: string;
  permissions: string[];
};

export type AuthContextValue = {
  user: AuthUser | null;
  isAuthenticated: boolean;
  /**
   * True while the provider resolves a stored session at boot — the access
   * token was missing/expired but a refresh token was present, so a silent
   * refresh is in flight. Routes render a loader (not the page, not a redirect)
   * while this is true, so a stale token never flashes a protected surface.
   */
  isInitializing: boolean;
  /**
   * True once permissions have been fetched at least once for the current
   * user (or no user is signed in). Route guards check this before rendering
   * a 403 — without it, the first paint flashes "access denied" while the
   * permissions request is still in flight.
   */
  permissionsHydrated: boolean;
  login: (input: { email: string; password: string; tenant: string }) => Promise<void>;
  logout: () => void;
  /** Re-fetch the permission set for the signed-in user. Call after a role
   *  assignment changes for the current user. */
  refreshPermissions: () => Promise<void>;

  /**
   * The tenant the operator is currently acting inside, or null. The operator's own session
   * keeps running underneath — `user` above is still them — and the acting token lives in
   * memory only (see acting-store).
   */
  acting: ActingSession | null;
  /** Exchange the operator's token for one that acts inside `tenantId`. */
  enterTenant: (input: {
    tenantId: string;
    tenantName?: string;
    reason: string;
    durationMinutes?: number;
  }) => Promise<ActingSession>;
  /** Leave the tenant: end the grant server-side (best effort) and drop the acting token. */
  exitTenant: () => Promise<void>;
};

export const AuthContext = createContext<AuthContextValue | null>(null);

function claimsToUser(claims: JwtClaims | null, permissions: string[]): AuthUser | null {
  if (!claims?.sub) return null;
  return {
    id: claims.sub,
    email: claims.email,
    name: claims.name,
    tenant: claims.tenant,
    permissions,
  };
}

// Read the stored session, treating an EXPIRED access token as "not usable
// yet": the boot effect attempts a silent refresh before we trust it. A
// decodable-but-expired token must not flip the app to authenticated, or it
// renders protected surfaces that 401 in a loop instead of refreshing.
function readStoredSession(): { claims: JwtClaims | null; usable: boolean } {
  const claims = decodeJwt(tokenStore.getAccessToken());
  return { claims, usable: claims !== null && !isTokenExpired(claims) };
}

export function AuthProvider({ children }: { children: ReactNode }) {
  const queryClient = useQueryClient();
  const [user, setUser] = useState<AuthUser | null>(() => {
    const { claims, usable } = readStoredSession();
    return usable ? claimsToUser(claims, tokenStore.getPermissions()) : null;
  });
  // When the stored access token is missing/expired but a refresh token is
  // present, attempt one silent refresh at boot before rendering — keeps
  // long-lived sessions alive AND stops a stale token from flashing a doomed
  // protected surface that fires 401-ing requests.
  const [isInitializing, setIsInitializing] = useState<boolean>(
    () => !readStoredSession().usable && tokenStore.getRefreshToken() !== null,
  );
  // Cold-start: if we already have a cached permissions list, treat as hydrated
  // so route guards don't flash 403. Otherwise, wait for the effect.
  const [permissionsHydrated, setPermissionsHydrated] = useState<boolean>(() => {
    if (!tokenStore.getAccessToken()) return true;
    return tokenStore.getPermissions().length > 0;
  });
  const lastHydratedSubject = useRef<string | null>(user?.id ?? null);

  useEffect(() => {
    if (!isInitializing) return;
    let cancelled = false;
    void (async () => {
      try {
        await refreshAccessToken();
      } catch {
        // Refresh token dead (expired, revoked, or DB reseeded) — drop the
        // stale session so routing falls through to /login cleanly.
        tokenStore.clear();
      } finally {
        if (!cancelled) setIsInitializing(false);
      }
    })();
    return () => {
      cancelled = true;
    };
    // Boot-only: isInitializing only ever flips false, never back on.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // Hydrate (or re-hydrate) the permissions list from the server whenever the
  // signed-in subject changes — covers cold-start, login, and account swap.
  useEffect(() => {
    if (!user) {
      lastHydratedSubject.current = null;
      setPermissionsHydrated(true);
      return;
    }
    if (lastHydratedSubject.current === user.id && permissionsHydrated) {
      return;
    }
    lastHydratedSubject.current = user.id;
    let cancelled = false;
    void (async () => {
      try {
        const perms = await getMyPermissions();
        if (cancelled) return;
        tokenStore.setPermissions(perms);
        // setPermissions emits, the subscribe listener will rebuild `user`
        // with the new list.
        setPermissionsHydrated(true);
      } catch {
        // Permissions fetch failure shouldn't sign the user out — the route
        // guards will treat them as zero-permission until the next refresh.
        if (!cancelled) setPermissionsHydrated(true);
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [user, permissionsHydrated]);

  useEffect(() => {
    return tokenStore.subscribe(() => {
      const next = claimsToUser(decodeJwt(tokenStore.getAccessToken()), tokenStore.getPermissions());
      setUser(next);
    });
  }, []);

  // Cross-tab auth sync. tokenStore.subscribe only fires for in-app mutations;
  // a `storage` event fires when ANOTHER tab logs in/out (e.g. inactivity
  // sign-out). Rebuild from the (now changed) tokens so a logout in one tab
  // drops every tab to /login. Scoped to the token keys so the inactivity
  // heartbeat ("boilerplate.lastActivity") doesn't trigger a rebuild every second.
  useEffect(() => {
    const onStorage = (e: StorageEvent) => {
      if (
        e.key !== null &&
        e.key !== "boilerplate.admin.accessToken" &&
        e.key !== "boilerplate.admin.refreshToken"
      ) {
        return;
      }
      setUser(claimsToUser(decodeJwt(tokenStore.getAccessToken()), tokenStore.getPermissions()));
    };
    window.addEventListener("storage", onStorage);
    return () => window.removeEventListener("storage", onStorage);
  }, []);

  const login = useCallback(
    async (input: { email: string; password: string; tenant: string }) => {
      tokenStore.setTenant(input.tenant);
      // Stale permissions from a previous user must not leak into the new
      // session — clear before issuing the token so the hydration effect
      // re-fetches from scratch.
      tokenStore.setPermissions([]);
      setPermissionsHydrated(false);
      const tokens = await issueToken(input);
      tokenStore.setTokens(tokens.accessToken, tokens.refreshToken);
    },
    [],
  );

  const logout = useCallback(() => {
    // Tell the server first — it revokes the session row and clears the HttpOnly refresh cookie,
    // neither of which this code can reach. Fire-and-forget: local state is cleared either way, so
    // a signed-out tab never waits on the network, and a failed call cannot strand the user.
    const tenant = tokenStore.getTenant();
    if (tenant) {
      void revokeSession({
        tenant,
        accessToken: tokenStore.getAccessToken(),
        refreshToken: tokenStore.getRefreshToken(),
      }).catch(() => {
        /* best-effort: the session expires on its own, and the local session is gone regardless */
      });
    }

    actingStore.clear();
    tokenStore.clear();
    queryClient.clear();
  }, [queryClient]);

  // ── Acting layer (operator token exchange, ADR-0002) ──────────────────
  const acting = useSyncExternalStore(actingStore.subscribe, actingStore.get);

  // An acting session can end without the operator asking (grant revoked, token expired). The
  // api client drops it and leaves a notice; surface that rather than silently switching
  // identity under the operator's feet.
  useEffect(() =>
    actingStore.subscribe(() => {
      const notice = actingStore.consumeNotice();
      if (notice) {
        toast.warning("Left the tenant", { description: notice });
        void queryClient.invalidateQueries();
      }
    }), [queryClient]);

  const enterTenant = useCallback(
    async (input: { tenantId: string; tenantName?: string; reason: string; durationMinutes?: number }) => {
      const exchanged = await exchangeOperatorToken({
        targetTenantId: input.tenantId,
        reason: input.reason,
        durationMinutes: input.durationMinutes,
      });

      const session: ActingSession = {
        accessToken: exchanged.accessToken,
        tenantId: exchanged.targetTenantId,
        tenantName: input.tenantName,
        userId: exchanged.targetUserId,
        userName: exchanged.targetUserName ?? undefined,
        expiresAt: exchanged.accessTokenExpiresAt,
        jti: exchanged.jti,
        grantId: exchanged.grantId,
      };
      actingStore.start(session);
      // Everything cached was fetched as the operator, in the operator's tenant.
      queryClient.clear();
      return session;
    },
    [queryClient],
  );

  const exitTenant = useCallback(async () => {
    try {
      // Best effort: ends the grant, so the exchanged token dies immediately instead of
      // lingering until expiry. Failing that, it expires on its own shortly.
      await endActingSession();
    } catch {
      /* ignore — the local drop below is what the operator actually asked for */
    } finally {
      actingStore.clear();
      queryClient.clear();
    }
  }, [queryClient]);

  const refreshPermissions = useCallback(async () => {
    try {
      const perms = await getMyPermissions();
      tokenStore.setPermissions(perms);
    } catch {
      /* swallow — see hydration effect */
    }
  }, []);

  const value = useMemo<AuthContextValue>(
    () => ({
      user,
      isAuthenticated: user !== null,
      isInitializing,
      permissionsHydrated,
      login,
      logout,
      refreshPermissions,
      acting,
      enterTenant,
      exitTenant,
    }),
    [user, isInitializing, permissionsHydrated, login, logout, refreshPermissions, acting, enterTenant, exitTenant],
  );

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}
