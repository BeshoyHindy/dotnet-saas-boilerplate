// Namespaced per client (ADR-0008): the console keeps its own `boilerplate.dashboard.*`
// keys, so a developer running both dev servers on one host never has one app's session
// read the other's.
const ACCESS_KEY = "boilerplate.dashboard.accessToken";
const TENANT_KEY = "boilerplate.dashboard.tenant";
const PERMS_KEY = "boilerplate.dashboard.permissions";

// There is no impersonation stash here, and no acting token: acting as someone else is
// the console's job (ADR-0008). This app only ever holds the signed-in user's own session.

type Listener = () => void;

const listeners = new Set<Listener>();

function emit() {
  for (const listener of listeners) listener();
}

/**
 * Session state the dashboard keeps in the browser.
 *
 * There is deliberately **no refresh token here**. The refresh token is an
 * `HttpOnly; Secure; SameSite=Strict` cookie the browser attaches on its own
 * (ADR-0002) and JavaScript never sees it — which is exactly why this app is
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
   * and whenever the signed-in subject changes.
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
    emit();
  },

  subscribe(listener: Listener) {
    listeners.add(listener);
    return () => {
      listeners.delete(listener);
    };
  },
};
