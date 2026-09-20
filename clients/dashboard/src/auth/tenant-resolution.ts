/**
 * Resolving the tenant at sign-in WITHOUT asking the user to type it.
 *
 * The tenant is the one thing a caller may name, and only on the anonymous auth URLs
 * (ADR-0002) — but "name" does not have to mean "type into a box". Almost every arrival
 * already carries the answer: a mailed link has it in the query string, a customer on their
 * own subdomain has it in the host, and a returning user had it last time.
 *
 * What this must NEVER become: a lookup. There is no endpoint that maps an email to its
 * tenants, and there must not be — that is user *and* tenant enumeration for an anonymous
 * caller. Everything here reads what the browser already knows.
 *
 * Pure functions, no React, no `window` reads of their own: the caller passes what it has,
 * so every branch is a unit test rather than a manual click-through.
 */

/** Where a resolved tenant came from. Decides how the form presents itself. */
export type TenantSource = "query" | "subdomain" | "remembered" | "default";

export type ResolvedTenant = {
  tenant: string;
  source: TenantSource;
  /**
   * True when the arrival itself named the tenant (a mailed link, or the customer's own
   * hostname). The field is then not rendered at all — there is nothing to ask. A
   * remembered or defaulted tenant is a guess, so the field stays visible and prefilled.
   */
  certain: boolean;
};

const REMEMBERED_KEY = "boilerplate.dashboard.lastTenant";

/**
 * Hosts whose first label is NOT a tenant. `localhost` and a bare apex (`example.com`) have
 * no tenant label at all; `www` is the marketing site. An IP literal has no labels in the
 * DNS sense — `127.0.0.1` must never resolve to tenant "127".
 */
const NON_TENANT_LABELS = new Set(["www", "app", "dashboard", "console", "api", "staging"]);

const IPV4 = /^\d{1,3}(\.\d{1,3}){3}$/;

/** Tenant identifiers the server accepts: slug-ish, and never something we invented. */
const TENANT_PATTERN = /^[a-z0-9][a-z0-9-]{0,62}$/i;

/** Strip the port, lowercase, drop a trailing dot (`acme.localhost.` is valid DNS). */
function normalizeHost(host: string): string {
  const withoutPort = host.replace(/:\d+$/, "");
  return withoutPort.toLowerCase().replace(/\.$/, "");
}

/**
 * The tenant named by the HOST, or null.
 *
 * A host carries a tenant when it has a first label AND something after it: `acme.localhost`
 * and `acme.app.example.com` both resolve to `acme`, because in each case the tenant label is
 * followed by a base the deployment owns. A bare host (`localhost`, `example.com`), an IP, or
 * a host whose first label is a known non-tenant name resolves to null — better to ask than
 * to sign someone in to a tenant called "www".
 *
 * Deliberately NOT configurable by base domain: the rule "first label, if there is a rest"
 * holds for `acme.localhost:5173` and `acme.app.example.com` alike, and a base-domain setting
 * would be one more thing to get wrong per environment. An apex deployment (`example.com`
 * serving one tenant) falls through to the remembered/default tenant, which is correct.
 */
export function tenantFromHost(host: string | undefined | null): string | null {
  if (!host) return null;
  const normalized = normalizeHost(host);
  if (!normalized || IPV4.test(normalized) || normalized.includes(":")) return null; // :: → IPv6 literal

  const labels = normalized.split(".");
  // Need a label AND a base after it. `localhost` → 1 label, `example.com` → an apex we
  // cannot distinguish from `tenant.tld`, so both are refused; the smallest host that
  // resolves is `tenant.localhost` or `tenant.example.com`... which is the same shape.
  if (labels.length < 2) return null;

  const [first] = labels;
  if (NON_TENANT_LABELS.has(first)) return null;
  if (!TENANT_PATTERN.test(first)) return null;
  // A two-label host is ambiguous (`example.com` vs `acme.localhost`). Only treat it as a
  // tenant when the base is a single-label one — i.e. `acme.localhost`, the dev shape —
  // otherwise require three labels, so `example.com` is an apex and `acme.app.example.com`
  // is a tenant.
  if (labels.length === 2 && labels[1] !== "localhost") return null;

  return first;
}

/** The tenant named by `?tenant=` on the URL, or null. Mailed links carry it. */
export function tenantFromQuery(search: string | undefined | null): string | null {
  if (!search) return null;
  const value = new URLSearchParams(search).get("tenant")?.trim();
  if (!value || !TENANT_PATTERN.test(value)) return null;
  return value;
}

/**
 * The tenant last signed in to on this device. localStorage only, wrapped — a Safari private
 * window throws on access rather than returning null, and a login page must not blank out
 * over a storage quirk. Only the tenant id is ever stored here; never a token.
 */
export function readRememberedTenant(): string | null {
  try {
    const value = localStorage.getItem(REMEMBERED_KEY)?.trim();
    return value && TENANT_PATTERN.test(value) ? value : null;
  } catch {
    return null;
  }
}

export function rememberTenant(tenant: string): void {
  try {
    if (TENANT_PATTERN.test(tenant)) localStorage.setItem(REMEMBERED_KEY, tenant);
  } catch {
    /* storage unavailable — the next visit just asks again */
  }
}

export function forgetRememberedTenant(): void {
  try {
    localStorage.removeItem(REMEMBERED_KEY);
  } catch {
    /* as above */
  }
}

/**
 * The order that decides which tenant a sign-in form starts with:
 *
 *   1. `?tenant=` — the arrival named it explicitly (mailed links do).
 *   2. the subdomain — the customer is on their own hostname.
 *   3. the tenant last used successfully on this device.
 *   4. the configured default.
 *
 * 1 and 2 are `certain`: the field is not shown at all. 3 and 4 are guesses: the field is
 * shown, prefilled, below email and password.
 */
export function resolveTenant(input: {
  search?: string | null;
  host?: string | null;
  remembered?: string | null;
  defaultTenant: string;
}): ResolvedTenant {
  const fromQuery = tenantFromQuery(input.search);
  if (fromQuery) return { tenant: fromQuery, source: "query", certain: true };

  const fromHost = tenantFromHost(input.host);
  if (fromHost) return { tenant: fromHost, source: "subdomain", certain: true };

  const remembered = input.remembered?.trim();
  if (remembered && TENANT_PATTERN.test(remembered)) {
    return { tenant: remembered, source: "remembered", certain: false };
  }

  return { tenant: input.defaultTenant, source: "default", certain: false };
}
