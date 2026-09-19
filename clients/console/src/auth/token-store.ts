const ACCESS_KEY = "boilerplate.console.accessToken";
const TENANT_KEY = "boilerplate.console.tenant";
const PERMS_KEY = "boilerplate.console.permissions";

// Impersonation stash. While an operator is inside another tenant, the live store
// holds the exchanged access token; the operator's own token and tenant sit under
// these keys so the End flow can restore them locally if the server call fails.
const STASH_ACCESS_KEY = "boilerplate.console.impersonation.actorAccessToken";
const STASH_TENANT_KEY = "boilerplate.console.impersonation.actorTenant";

type Listener = () => void;

const listeners = new Set<Listener>();

function emit() {
  for (const listener of listeners) listener();
}

/**
 * Session state the console keeps in the browser.
 *
 * There is deliberately **no refresh token here**. The refresh token is an
 * `HttpOnly; Secure; SameSite=Strict` cookie the browser attaches on its own
 * (ADR-0002) and JavaScript never sees it — which is exactly why the console is
 * served from the API's origin, with nginx proxying `/api`. What is stored is the
 * short-lived access token, the tenant **Id** (the refresh cookie's `Path` names
 * it, so the identifier typed at sign-in would not do), and the permission set.
 */
export const tokenStore = {
  getAccessToken: () => localStorage.getItem(ACCESS_KEY),
  getTenant: () => localStorage.getItem(TENANT_KEY),

  /**
   * Permissions are fetched separately from the JWT (the token only carries
   * role names — see GetCurrentUserPermissionsEndpoint server-side). Cached
   * here so gated UI can read them synchronously; re-hydrated on each login
   * and whenever the signed-in subject changes (incl. impersonation swaps).
   */
  getPermissions(): string[] {
    try {
      const raw = localStorage.getItem(PERMS_KEY);
      if (!raw) return [];
      const parsed = JSON.parse(raw) as unknown;
      return Array.isArray(parsed) ? parsed.filter((p): p is string => typeof p === "string") : [];
    } catch {
      return [];
    }
  },

  setPermissions(permissions: string[]) {
    localStorage.setItem(PERMS_KEY, JSON.stringify(permissions));
    emit();
  },

  setAccessToken(accessToken: string) {
    localStorage.setItem(ACCESS_KEY, accessToken);
    emit();
  },

  setTenant(tenant: string) {
    localStorage.setItem(TENANT_KEY, tenant);
    emit();
  },

  clear() {
    localStorage.removeItem(ACCESS_KEY);
    localStorage.removeItem(PERMS_KEY);
    // Also clear any impersonation stash so a fresh login doesn't
    // inherit half of a previous operator's session.
    localStorage.removeItem(STASH_ACCESS_KEY);
    localStorage.removeItem(STASH_TENANT_KEY);
    emit();
  },

  /**
   * Swap the active token for an exchanged one representing a user in another
   * tenant, keeping the operator's own token locally. The exchanged token is
   * access-only server-side, and the API client refuses to refresh while it is
   * installed (there is no refresh cookie for the target tenant).
   */
  beginImpersonation(impersonationAccessToken: string, impersonatedTenant: string | null) {
    const access = localStorage.getItem(ACCESS_KEY);
    const tenant = localStorage.getItem(TENANT_KEY);
    if (access) localStorage.setItem(STASH_ACCESS_KEY, access);
    if (tenant) localStorage.setItem(STASH_TENANT_KEY, tenant);

    localStorage.setItem(ACCESS_KEY, impersonationAccessToken);
    // Drop the operator's permissions — the impersonated subject has its own;
    // the auth context re-hydrates on the subject change.
    localStorage.removeItem(PERMS_KEY);
    if (impersonatedTenant) localStorage.setItem(TENANT_KEY, impersonatedTenant);
    emit();
  },

  /**
   * Install the fresh actor access token returned by the End Impersonation
   * endpoint and clear the stash. Use this on End success.
   *
   * End is access-only (the server cannot write a session row in the actor's
   * tenant from the impersonated tenant's context), which costs nothing here:
   * the operator's own session — and its refresh cookie — was never revoked.
   */
  endImpersonationWithFreshAccessToken(accessToken: string) {
    const stashTenant = localStorage.getItem(STASH_TENANT_KEY);
    localStorage.setItem(ACCESS_KEY, accessToken);
    localStorage.removeItem(PERMS_KEY);
    if (stashTenant) localStorage.setItem(TENANT_KEY, stashTenant);
    localStorage.removeItem(STASH_ACCESS_KEY);
    localStorage.removeItem(STASH_TENANT_KEY);
    emit();
  },

  /**
   * Last-resort local restore — used if the End endpoint fails. Reinstalls the
   * stashed operator token so they at least have *some* session; if it has since
   * expired, the next 401 refreshes it against their own tenant's cookie.
   */
  restoreStashedActor(): boolean {
    const access = localStorage.getItem(STASH_ACCESS_KEY);
    const tenant = localStorage.getItem(STASH_TENANT_KEY);
    if (!access) return false;
    localStorage.setItem(ACCESS_KEY, access);
    localStorage.removeItem(PERMS_KEY);
    if (tenant) localStorage.setItem(TENANT_KEY, tenant);
    localStorage.removeItem(STASH_ACCESS_KEY);
    localStorage.removeItem(STASH_TENANT_KEY);
    emit();
    return true;
  },

  hasImpersonationStash: () => localStorage.getItem(STASH_ACCESS_KEY) !== null,

  subscribe(listener: Listener) {
    listeners.add(listener);
    return () => {
      listeners.delete(listener);
    };
  },
};
