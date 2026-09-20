import createClient from "openapi-fetch";
import { env } from "@/env";
import { tokenStore } from "@/auth/token-store";
import { endSessionLocally } from "@/lib/query-client";
import type { paths, components } from "@/api/schema";

/**
 * The one HTTP client (ADR-0008). Every request goes through `api`, an
 * `openapi-fetch` client typed by `src/api/schema.d.ts` — which is generated from the
 * checked-in contract `clients/openapi/v1.json` by `pnpm generate:api`. There are no
 * hand-written request or response types anywhere in `src/api`; a DTO is always
 * `components["schemas"][…]`.
 *
 * One credential, always: the signed-in user's own access token. The acting layer (an
 * operator's token exchange, and the `X-Console-As-Operator` opt-out that goes with it)
 * lives in the console — this app never holds a credential naming somebody else.
 */
export type Schemas = components["schemas"];

/**
 * The paged envelope every search endpoint returns, with the item type swapped in.
 * Derived from a generated page type rather than declared, so a change to the
 * envelope on the server lands here through `pnpm generate:api`.
 */
export type Paged<T> = Omit<Schemas["PagedResponseOfUserDto"], "items"> & { items: T[] };

export type ApiError = {
  status: number;
  title?: string;
  detail?: string;
  // FluentValidation errors arrive keyed by field (Record); CustomException
  // (e.g. Identity registration failures) sends a flat string[]. Handle both.
  errors?: Record<string, string[]> | string[];
  // Dev-only extension surfaced on 401 by ConfigureJwtBearerOptions.
  reason?: string;
  // Allow any other ProblemDetails extensions through.
  [key: string]: unknown;
};

export class ApiRequestError extends Error {
  readonly status: number;
  readonly problem?: ApiError;

  constructor(status: number, message: string, problem?: ApiError) {
    super(message);
    this.status = status;
    this.problem = problem;
  }
}

/**
 * True when an error is the API's "tenant has been deactivated" 403. The
 * deactivated-tenant guard (MultitenancyModule) rejects *every* request once a
 * tenant is switched off, so this can surface from any query/mutation while a
 * user is mid-session. There is no machine-readable code on the ProblemDetails,
 * so we match the guard's detail text. A global query/mutation error hook uses
 * this to route the user to the dedicated `/tenant-deactivated` page rather than
 * leaving the dead 403 banner stuck under a half-loaded surface.
 */
export function isTenantDeactivatedError(error: unknown): boolean {
  if (!(error instanceof ApiRequestError) || error.status !== 403) return false;
  const detail = error.problem?.detail ?? error.message ?? "";
  return detail.toLowerCase().includes("tenant has been deactivated");
}

/**
 * The anonymous, tenant-scoped auth routes. The tenant travels in the path because
 * no token exists yet on these calls (ADR-0002); every other request carries its
 * tenant inside the signed token, so the client never names a tenant on the wire.
 *
 * `tenant` must be the tenant **Id** once one is known: the refresh cookie's `Path`
 * is `/api/v1/tenants/{tenantId}/auth/refresh`, so refreshing under the identifier
 * the user typed at sign-in would send no cookie at all.
 */
export function authPath(tenant: string, segment: string): string {
  return `/api/v1/tenants/${encodeURIComponent(tenant)}/auth/${segment}`;
}

const DEFAULT_TIMEOUT_MS = 30_000;

/** Absolute URL for a path, honouring a non-empty runtime `apiBase`. */
export function apiUrl(path: string): string {
  return path.startsWith("http") ? path : `${env.apiBase}${path}`;
}

let refreshPromise: Promise<void> | null = null;

/**
 * Rotate the access token. The refresh token itself is an `HttpOnly; SameSite=Strict`
 * cookie the browser attaches on its own (ADR-0002) — the dashboard never sees it, which
 * is why it is served from the same origin as the API (nginx proxies `/api`). The body
 * carries only the expired access token, which the server cross-checks.
 */
export async function refreshAccessToken(): Promise<void> {
  const accessToken = tokenStore.getAccessToken();
  const tenant = tokenStore.getTenant();
  if (!accessToken || !tenant) {
    throw new ApiRequestError(401, "No session to refresh");
  }

  const response = await fetch(apiUrl(authPath(tenant, "refresh")), {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    // Same-origin by construction; spelled out so a misconfigured deployment fails
    // loudly rather than silently refreshing without the cookie.
    credentials: "same-origin",
    body: JSON.stringify({ token: accessToken }),
    // A stalled refresh would otherwise hang forever and block every queued
    // 401-retry awaiting the shared refreshPromise.
    signal: AbortSignal.timeout(DEFAULT_TIMEOUT_MS),
  });

  if (!response.ok) {
    // The refresh cookie is dead (expired, revoked, or rotated by another tab) — end the
    // session locally rather than leaving a stray acting token or stale cache behind for
    // whoever signs in next in this tab.
    endSessionLocally();
    throw new ApiRequestError(response.status, "Refresh failed");
  }

  // The rotated access token arrives on `token`, not `accessToken`.
  const tokens = (await response.json()) as Schemas["RefreshTokenCommandResponse"];
  tokenStore.setAccessToken(tokens.token ?? "");
}

/**
 * One refresh at a time, shared by every 401 that lands while it is in flight.
 *
 * The slot is released only once the shared promise has settled *for the caller that
 * created it*. Chaining the release onto the inner promise (`p.finally(() => slot = null)`)
 * frees it a microtask early — before the promise the other callers are awaiting resolves —
 * and a 401 arriving in that window starts a second refresh, rotating the refresh cookie
 * twice and invalidating the token the first rotation just handed out.
 */
async function sharedRefresh(): Promise<void> {
  const inFlight = refreshPromise;
  if (inFlight !== null) return inFlight;

  const started = refreshAccessToken();
  refreshPromise = started;
  try {
    await started;
  } finally {
    // Only the owner clears the slot, and only if nothing has replaced it since.
    if (refreshPromise === started) refreshPromise = null;
  }
}

/** Paths the client calls without a bearer token (they mint or reset one). */
function isAnonymous(url: string): boolean {
  return /\/api\/v1\/tenants\/[^/]+\/auth\//.test(url);
}

/**
 * The `fetch` every typed call runs on: bearer header, request timeout, and a
 * single-flight refresh-and-retry on 401. Concurrent 401s share one refresh.
 */
async function authFetch(input: Request): Promise<Response> {
  const anonymous = isAnonymous(input.url);
  const target = env.apiBase && !input.url.startsWith(env.apiBase)
    ? new Request(apiUrl(new URL(input.url).pathname + new URL(input.url).search), input)
    : input;

  const send = async (): Promise<Response> => {
    const request = target.clone();
    if (!anonymous) {
      const accessToken = tokenStore.getAccessToken();
      if (!accessToken) {
        // Not anonymous but the token is gone — likely a manual localStorage clear
        // AuthProvider missed. Surface a clean 401 so the UI flips to /login instead
        // of firing tokenless requests forever. End the session fully so nothing
        // cached from before the clear can outlive it.
        endSessionLocally();
        throw new ApiRequestError(401, "Not signed in", {
          status: 401,
          title: "Unauthorized",
          detail: "Your session is no longer available. Please sign in again.",
        });
      }
      request.headers.set("Authorization", `Bearer ${accessToken}`);
    }
    // Browser fetch has no default timeout, so a stalled request would hang React
    // Query's promise forever. The caller's own signal (React Query cancellation,
    // page unmount) still has to win, hence the union rather than a replacement.
    return fetch(request, {
      signal: AbortSignal.any([request.signal, AbortSignal.timeout(DEFAULT_TIMEOUT_MS)]),
    });
  };

  let response = await send();

  if (response.status === 401 && !anonymous) {
    try {
      await sharedRefresh();
    } catch (error) {
      throw error instanceof ApiRequestError ? error : new ApiRequestError(401, "Session expired");
    }

    response = await send();
  }

  return response;
}

export const api = createClient<paths>({ fetch: authFetch });

/** The shape every `openapi-fetch` call resolves to, narrowed to what `unwrap` needs. */
type ApiResult<T> = { data?: T; error?: unknown; response: Response };

/**
 * Turn an `openapi-fetch` result into the value a caller wants, or throw.
 * Non-OK responses carry RFC 9457 `application/problem+json` on `error`; 204 and
 * empty bodies resolve to `undefined`, which is what the void callers return.
 */
export function unwrap<T>(result: ApiResult<T>): T {
  if (result.error !== undefined || !result.response.ok) {
    const problem = (result.error ?? undefined) as ApiError | undefined;
    throw new ApiRequestError(
      result.response.status,
      problem?.title ?? problem?.detail ?? result.response.statusText,
      problem,
    );
  }
  return result.data as T;
}

/** `unwrap` for endpoints whose success response has no body (204). */
export function unwrapVoid<T>(result: ApiResult<T>): void {
  unwrap(result);
}
