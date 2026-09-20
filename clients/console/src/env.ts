// Runtime config — fetched once at boot from /config.json, never baked into the
// bundle: one built image promotes across environments, and the container
// entrypoint renders this file from APP_* variables at start (ADR-0008). The one
// origin named here is the tenant app's, and only so a tenant user who lands on the
// operator console can be sent where they meant to go.
type RuntimeConfig = {
  apiBase: string;
  defaultTenant: string;
  /**
   * Where the tenant app lives, if it is deployed. Used only to point a non-operator who
   * signed in here at the app that is actually theirs (ADR-0008). Empty = no link shown;
   * nothing functional depends on it, and the console never calls it.
   */
  dashboardUrl: string;
  /**
   * Show the demo affordance on the sign-in page. OFF unless a deployment says otherwise.
   * Unlike the dashboard's picker there is no password here: the seeded root admin's
   * password is its own parameter (`Seed__DefaultAdminPassword`), not the demo one, so the
   * console only ever prefills the operator's email.
   */
  demoMode: boolean;
  /** The seeded root operator's email — what the affordance fills in. */
  demoOperatorEmail: string;
  /** Idle time (ms) before the inactivity warning appears. */
  inactivityIdleMs: number;
  /** Warning-countdown length (ms) before auto sign-out. */
  inactivityWarningMs: number;
};

// Dashboard defaults: 20 minutes idle, then a 60-second warning.
const DEFAULT_INACTIVITY_IDLE_MS = 20 * 60_000;
const DEFAULT_INACTIVITY_WARNING_MS = 60_000;

/** Accept a positive finite number from config, else fall back. */
function positiveOr(value: unknown, fallback: number): number {
  return typeof value === "number" && Number.isFinite(value) && value > 0 ? value : fallback;
}

let cached: RuntimeConfig | null = null;

export async function loadRuntimeConfig(): Promise<void> {
  if (cached !== null) return;
  const res = await fetch("/config.json", { cache: "no-store" });
  if (!res.ok) {
    throw new Error(`Failed to load /config.json: ${res.status} ${res.statusText}`);
  }
  const cfg = (await res.json()) as Partial<RuntimeConfig>;
  cached = {
    apiBase: (cfg.apiBase ?? "").replace(/\/$/, ""),
    defaultTenant: cfg.defaultTenant ?? "root",
    dashboardUrl: (cfg.dashboardUrl ?? "").replace(/\/$/, ""),
    demoMode: import.meta.env.DEV
      ? import.meta.env.VITE_DEMO_MODE === "true" || cfg.demoMode === true
      : cfg.demoMode === true,
    demoOperatorEmail: cfg.demoOperatorEmail || "admin@root.com",
    inactivityIdleMs: positiveOr(cfg.inactivityIdleMs, DEFAULT_INACTIVITY_IDLE_MS),
    inactivityWarningMs: positiveOr(cfg.inactivityWarningMs, DEFAULT_INACTIVITY_WARNING_MS),
  };
}

function get(): RuntimeConfig {
  if (cached === null) {
    throw new Error(
      "Runtime config not loaded. main.tsx must await loadRuntimeConfig() before mounting React.",
    );
  }
  return cached;
}

export const env = {
  get apiBase(): string { return get().apiBase; },
  get defaultTenant(): string { return get().defaultTenant; },
  get dashboardUrl(): string { return get().dashboardUrl; },
  get demoMode(): boolean { return get().demoMode; },
  get demoOperatorEmail(): string { return get().demoOperatorEmail; },
  get inactivityIdleMs(): number { return get().inactivityIdleMs; },
  get inactivityWarningMs(): number { return get().inactivityWarningMs; },
};
