import {
  createContext,
  useCallback,
  useEffect,
  useMemo,
  useRef,
  useState,
  useSyncExternalStore,
  type ReactNode,
} from "react";
import { useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { tokenStore } from "@/auth/token-store";
import { actingStore, type ActingSession } from "@/auth/acting-store";
import { decodeJwt, isTokenExpired, type JwtClaims } from "@/auth/jwt";
import { endSession, issueToken } from "@/auth/api";
import { refreshAccessToken } from "@/lib/api-client";
import { endSessionLocally } from "@/lib/query-client";
import { getMyPermissions } from "@/api/identity";
import { endActingSession, exchangeOperatorToken, startImpersonation } from "@/api/operator";

/**
 * `ActingSession` minus the bearer credential — what a component is allowed to see.
 * The transport (`src/lib/api-client.ts`) reads the real session, `accessToken`
 * included, straight off `acting-store`; nothing else needs to hold it.
 */
export type ActingSessionView = Omit<ActingSession, "accessToken">;

function toActingView(session: ActingSession | null): ActingSessionView | null {
  if (!session) return null;
  const { tenantId, tenantName, userId, userName, expiresAt, jti, grantId } = session;
  return { tenantId, tenantName, userId, userName, expiresAt, jti, grantId };
}

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
   * while this is true, so a stale token never flashes a doomed dashboard.
   */
  isInitializing: boolean;
  /**
   * True once permissions have been fetched at least once for the current
   * subject (or no user is signed in). Lets gated UI avoid flashing while the
   * permissions request is still in flight.
   */
  permissionsHydrated: boolean;
  login: (input: { email: string; password: string; tenant: string }) => Promise<void>;
  logout: () => void;
  /** Re-fetch the permission set for the signed-in user (e.g. after a role change). */
  refreshPermissions: () => Promise<void>;

  /**
   * The session the user is currently acting through, or null — METADATA ONLY, no
   * `accessToken`. Their own session keeps running underneath — `user` above is still
   * them. The acting token itself stays reachable only through `acting-store`, inside
   * the transport (`src/lib/api-client.ts`): every component gets to know *that* and
   * *who* the caller is acting as, never the bearer credential it takes to do it.
   */
  acting: ActingSessionView | null;
  /** Exchange the operator's token for one that acts inside `tenantId` (root only). */
  enterTenant: (input: {
    tenantId: string;
    tenantName?: string;
    /** Act as this user rather than the tenant's own admin. */
    targetUserId?: string;
    reason: string;
    durationMinutes?: number;
  }) => Promise<ActingSession>;
  /**
   * Impersonate a user in the CALLER's own tenant. Same acting layer, different grant:
   * crossing a tenant boundary is `enterTenant`, and the server refuses it here.
   */
  impersonateInOwnTenant: (input: {
    targetUserId: string;
    targetTenantId: string;
    userName?: string;
    tenantName?: string;
    reason?: string;
    durationMinutes?: number;
  }) => Promise<ActingSession>;
  /** Stop acting: end the grant server-side (best effort) and drop the acting token. */
  exitTenant: () => Promise<void>;
};

export const AuthContext = createContext<AuthContextValue | null>(null);

// Permissions are NOT in the JWT (it carries only role names). They're fetched
// from /api/v1/identity/permissions and cached in the token store; this builds
// the user from the token's identity claims + that separately-hydrated list.
function claimsToUser(claims: JwtClaims | null, permissions: string[]): AuthUser | null {
  if (!claims?.sub) return null;
  // `name` is the standard short claim; `unique_name` is what
  // JwtSecurityTokenHandler emits for ClaimTypes.Name. Treat empty
  // strings as missing so the topbar falls through to email/Unknown
  // instead of rendering blank.
  const name = pickFirstNonEmpty(claims.name, claims.unique_name);
  const email = pickFirstNonEmpty(claims.email);
  return {
    id: claims.sub,
    email,
    name,
    tenant: claims.tenant,
    permissions,
  };
}

function pickFirstNonEmpty(...candidates: Array<string | undefined>): string | undefined {
  for (const c of candidates) {
    if (typeof c === "string" && c.trim().length > 0) return c;
  }
  return undefined;
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
  // Hydrated once permissions have been fetched for the current subject. Seed
  // from any cached list so a warm reload doesn't flash ungated UI.
  const [permissionsHydrated, setPermissionsHydrated] = useState<boolean>(() => {
    if (!tokenStore.getAccessToken()) return true;
    return tokenStore.getPermissions().length > 0;
  });
  const lastHydratedSubject = useRef<string | null>(null);
  // When the stored access token is missing or expired but a tenant is remembered,
  // attempt one silent refresh at boot before rendering. The refresh token itself
  // is an HttpOnly cookie this code cannot see (ADR-0002), so a remembered tenant —
  // which is what names the cookie's path — is the only signal that a session may
  // still be recoverable. It also stops a stale token from flashing a doomed
  // console that fires 401-ing requests.
  const [isInitializing, setIsInitializing] = useState<boolean>(
    () => !readStoredSession().usable && tokenStore.getTenant() !== null,
  );

  useEffect(() => {
    if (!isInitializing) return;
    let cancelled = false;
    void (async () => {
      try {
        await refreshAccessToken();
      } catch {
        // Refresh token dead (expired, revoked, or DB reseeded) — end the session so
        // routing falls through to /login cleanly, and so a stray acting token or
        // cached query from before the reload cannot outlive it.
        endSessionLocally();
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

  // Hydrate (or re-hydrate) the permission list from the server whenever the
  // signed-in subject changes — cold-start and login. Permissions live server-side
  // per role, not in the JWT.
  //
  // Always asked for as the OPERATOR (AS_OPERATOR): while acting, this endpoint answers
  // for the user being acted as, and caching a stranger's grants as "my permissions"
  // would regate the operator's own chrome — hiding the very screens they entered from.
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
        const perms = await getMyPermissions({ asOperator: true });
        if (cancelled) return;
        // setPermissions emits → the subscribe listener rebuilds `user` with the list.
        tokenStore.setPermissions(perms);
        setPermissionsHydrated(true);
      } catch {
        // A fetch failure must not sign the user out — gated UI just stays
        // hidden until the next successful hydration.
        if (!cancelled) setPermissionsHydrated(true);
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [user, permissionsHydrated]);

  useEffect(() => {
    const refresh = () => {
      const claims = decodeJwt(tokenStore.getAccessToken());
      setUser(claimsToUser(claims, tokenStore.getPermissions()));
    };
    const unsubscribe = tokenStore.subscribe(refresh);

    // The token store's subscribe() only fires for in-app mutations. Storage
    // changes from another tab fire a `storage` event, and same-tab manual
    // clears (e.g. via DevTools) need to be picked up when the user returns
    // to the tab — otherwise `isAuthenticated` stays true while the token
    // is gone, and protected requests silently 401 with no header attached.
    const onStorage = (e: StorageEvent) => {
      if (e.key === null || e.key.startsWith("boilerplate.console.")) refresh();
    };
    const onVisibility = () => {
      if (document.visibilityState === "visible") refresh();
    };
    window.addEventListener("storage", onStorage);
    document.addEventListener("visibilitychange", onVisibility);

    return () => {
      unsubscribe();
      window.removeEventListener("storage", onStorage);
      document.removeEventListener("visibilitychange", onVisibility);
    };
  }, []);

  const login = useCallback(
    async (input: { email: string; password: string; tenant: string }) => {
      // The identifier the user typed only gets us through the login URL. Stale
      // permissions from a previous user must not leak into the new session —
      // clear them before issuing the token so hydration re-fetches from scratch.
      tokenStore.setPermissions([]);
      setPermissionsHydrated(false);
      // Whatever the PREVIOUS user in this tab was acting as must not survive into this
      // one: the acting token is in-memory only and keyed to nothing that changes on
      // sign-in, so without this a forced sign-out (or a shared machine) would hand the
      // next person who logs in someone else's acting session.
      actingStore.clear();
      const tokens = await issueToken(input);
      // Remember the tenant **Id** from the token, not what was typed: the refresh
      // cookie's Path is /api/v1/tenants/{tenantId}/auth/refresh, so refreshing
      // under a renameable identifier would send no cookie at all (ADR-0002).
      const claims = decodeJwt(tokens.accessToken);
      tokenStore.setTenant(claims?.tenant ?? input.tenant);
      // Only the access token is kept; the refresh token stays in the HttpOnly
      // cookie the same response set. Root operators sign in here too — there is
      // one console, and operator screens sit behind permissions (ADR-0004).
      tokenStore.setAccessToken(tokens.accessToken);
      // Drop any cached query state from before login. Without this, a
      // failed pre-login probe (e.g. OverviewPage's tenant-status fetch
      // firing during the brief window before ProtectedRoute redirects
      // to /login, or a stale error from a previous session) sticks in
      // the react-query cache as a 401 and renders as an ErrorBand on
      // the next page — react-query's retry config blocks auto-retries
      // for 401, so the stale error would never refetch on its own.
      queryClient.clear();
    },
    [queryClient],
  );

  const logout = useCallback(() => {
    // Tell the server first — it revokes the session row and clears the HttpOnly refresh cookie,
    // neither of which this code can reach. Fire-and-forget: local state is cleared either way, so
    // a signed-out tab never waits on the network, and a failed call cannot strand the user.
    //
    // It always names the signed-in user's own tenant and rides on their own session: the acting
    // token, if any, is a separate in-memory credential that `actingStore.clear()` below disposes
    // of. Its grant is left to expire — signing out is not the place to await a second round trip.
    const tenant = tokenStore.getTenant();
    if (tenant) {
      void endSession(tenant).catch(() => {
        /* best-effort: the session expires on its own, and the local session is gone regardless */
      });
    }

    // The acting token is a separate credential and must not outlive the session that
    // minted it, even in memory — endSessionLocally() is the one place that drops it
    // alongside the token store and the query cache (see .agents/rules/frontend/console.md,
    // "The acting token"; the clear-site rule itself is in clients.md).
    endSessionLocally();
  }, []);

  // ── Acting layer (operator token exchange + impersonation, ADR-0002) ──
  const rawActing = useSyncExternalStore(actingStore.subscribe, actingStore.get);
  // Projected to metadata only — see `ActingSessionView`. Memoized on the underlying
  // session (a stable reference between store changes) so this doesn't manufacture a
  // new object, and therefore a new `value` below, on every unrelated re-render.
  const acting = useMemo(() => toActingView(rawActing), [rawActing]);

  // An acting session can end without being asked to (grant revoked, token expired). The
  // API client drops it and leaves a notice; surface that rather than silently switching
  // identity under the operator's feet.
  useEffect(
    () =>
      actingStore.subscribe(() => {
        const notice = actingStore.consumeNotice();
        if (notice) {
          toast.warning("Stopped acting", { description: notice });
          // clear(), not invalidateQueries(): everything cached was fetched under the
          // dropped acting credential, in another tenant. Invalidating only marks it
          // stale — it can still render (and refetch-fail loudly) before the queries
          // that matter finish; clear() empties the cache outright, matching enter/exit.
          queryClient.clear();
        }
      }),
    [queryClient],
  );

  // Everything cached was fetched under the previous credential, in the previous tenant.
  const installActing = useCallback(
    (session: ActingSession) => {
      actingStore.start(session);
      queryClient.clear();
      return session;
    },
    [queryClient],
  );

  const enterTenant = useCallback(
    async (input: {
      tenantId: string;
      tenantName?: string;
      targetUserId?: string;
      reason: string;
      durationMinutes?: number;
    }) => {
      const exchanged = await exchangeOperatorToken({
        targetTenantId: input.tenantId,
        targetUserId: input.targetUserId,
        reason: input.reason,
        durationMinutes: input.durationMinutes,
      });

      return installActing({
        accessToken: exchanged.accessToken,
        tenantId: exchanged.targetTenantId,
        tenantName: input.tenantName,
        userId: exchanged.targetUserId,
        userName: exchanged.targetUserName ?? undefined,
        expiresAt: exchanged.accessTokenExpiresAt,
        jti: exchanged.jti,
        grantId: exchanged.grantId,
      });
    },
    [installActing],
  );

  const impersonateInOwnTenant = useCallback(
    async (input: {
      targetUserId: string;
      targetTenantId: string;
      userName?: string;
      tenantName?: string;
      reason?: string;
      durationMinutes?: number;
    }) => {
      const response = await startImpersonation({
        targetUserId: input.targetUserId,
        targetTenantId: input.targetTenantId,
        reason: input.reason,
        durationMinutes: input.durationMinutes,
      });

      return installActing({
        accessToken: response.accessToken,
        tenantId: response.impersonatedTenantId,
        tenantName: input.tenantName,
        userId: response.impersonatedUserId,
        userName: input.userName,
        expiresAt: response.accessTokenExpiresAt,
        // Same-tenant start returns no grant id; the token's own jti is what scopes the
        // caches, and the grants list finds the row by subject.
        jti: decodeJwt(response.accessToken)?.jti,
      });
    },
    [installActing],
  );

  const exitTenant = useCallback(async () => {
    try {
      // Best effort: ends the grant, so the acting token dies immediately instead of
      // lingering until expiry. Failing that, it expires on its own shortly.
      await endActingSession();
    } catch {
      /* ignore — the local drop below is what the user actually asked for */
    } finally {
      actingStore.clear();
      queryClient.clear();
    }
  }, [queryClient]);

  const refreshPermissions = useCallback(async () => {
    try {
      const perms = await getMyPermissions({ asOperator: true });
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
      impersonateInOwnTenant,
      exitTenant,
    }),
    [
      user,
      isInitializing,
      permissionsHydrated,
      login,
      logout,
      refreshPermissions,
      acting,
      enterTenant,
      impersonateInOwnTenant,
      exitTenant,
    ],
  );

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}
