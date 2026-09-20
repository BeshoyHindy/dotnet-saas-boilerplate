// Runtime config — fetched once at boot from /config.json, never baked into the
// bundle: one built image promotes across environments, and the container
// entrypoint renders this file from APP_* variables at start (ADR-0008). Nothing
// here names another origin — the console is a separate deployment this app never
// calls.
type RuntimeConfig = {
  apiBase: string;
  /** Tenant the sign-in form falls back to when nothing else resolves one. */
  defaultTenant: string;
  /**
   * Show the demo-account picker on the sign-in page. OFF unless a deployment says
   * otherwise: it advertises real accounts, so it belongs to a throwaway environment.
   */
  demoMode: boolean;
  /**
   * Shared password for those demo accounts. Runtime config precisely so it is NOT a
   * literal in the repository. Empty is a supported state: the picker then fills the
   * tenant and email and asks for the password instead of signing in.
   */
  demoPassword: string;
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
    // Demo mode is the ONE setting a dev server may override, because `public/config.json`
    // is a checked-in file and turning demo mode on in it would commit that choice — and,
    // with the password, a credential. In a built image these are always `/config.json`'s,
    // rendered by the entrypoint from APP_DEMO_MODE / APP_DEMO_PASSWORD.
    demoMode: import.meta.env.DEV
      ? import.meta.env.VITE_DEMO_MODE === "true" || cfg.demoMode === true
      : cfg.demoMode === true,
    demoPassword: import.meta.env.DEV
      ? (import.meta.env.VITE_DEMO_PASSWORD ?? (typeof cfg.demoPassword === "string" ? cfg.demoPassword : ""))
      : (typeof cfg.demoPassword === "string" ? cfg.demoPassword : ""),
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
  get demoMode(): boolean { return get().demoMode; },
  get demoPassword(): string { return get().demoPassword; },
  get inactivityIdleMs(): number { return get().inactivityIdleMs; },
  get inactivityWarningMs(): number { return get().inactivityWarningMs; },
};
