import { api, unwrap, unwrapVoid, type Schemas } from "@/lib/api-client";

// ─────────────────────────────────────────────────────────────────────────
// Tenant status / validity
//
// Drives the global expiry/grace banner and the overview page's "Valid for" stat —
// the tenant's validity window, not a subscription plan. Only the caller's OWN tenant
// is reachable from here; listing, creating and renewing tenants is the console's
// (ADR-0008), and the endpoints behind them refuse a non-root caller anyway.
// ─────────────────────────────────────────────────────────────────────────

export type TenantExpiryState = "Active" | "InGrace" | "Expired" | (string & {});

export type TenantStatusDto = Schemas["TenantStatusDto"];

/** Fetch the current tenant's validity status. */
export async function getMyStatus(): Promise<TenantStatusDto> {
  return unwrap(await api.GET("/api/v1/tenants/me/status", {}));
}

// ─────────────────────────────────────────────────────────────────────────
// Tenant theme / branding
//
// The theme endpoints are CURRENT-TENANT scoped server-side: they act on the tenant the
// caller's token names, and a caller cannot name another one (ADR-0002). That is exactly
// what the settings/branding page needs — a tenant editing its own brand.
// ─────────────────────────────────────────────────────────────────────────

export type PaletteDto = Schemas["PaletteDto"];
export type BrandAssetsDto = Schemas["BrandAssetsDto"];
export type TypographyDto = Schemas["TypographyDto"];
export type LayoutDto = Schemas["LayoutDto"];
export type TenantThemeDto = Schemas["TenantThemeDto"];

export const DEFAULT_LIGHT_PALETTE: PaletteDto = {
  primary: "#2563EB",
  secondary: "#0F172A",
  tertiary: "#6366F1",
  background: "#F8FAFC",
  surface: "#FFFFFF",
  error: "#DC2626",
  warning: "#F59E0B",
  success: "#16A34A",
  info: "#0284C7",
};

export const DEFAULT_DARK_PALETTE: PaletteDto = {
  primary: "#38BDF8",
  secondary: "#94A3B8",
  tertiary: "#818CF8",
  background: "#0B1220",
  surface: "#111827",
  error: "#F87171",
  warning: "#FBBF24",
  success: "#22C55E",
  info: "#38BDF8",
};

/** Fetch the caller's tenant theme. Needs MultitenancyPermissions.Tenants.ViewTheme. */
export async function getTenantTheme(): Promise<TenantThemeDto> {
  return unwrap(await api.GET("/api/v1/tenants/theme", {}));
}

/** Save the caller's tenant theme. Needs MultitenancyPermissions.Tenants.UpdateTheme. */
export async function updateTenantTheme(theme: TenantThemeDto): Promise<void> {
  unwrapVoid(await api.PUT("/api/v1/tenants/theme", { body: theme }));
}

/**
 * A comparable snapshot of a theme draft, for the editors' "unsaved" check.
 *
 * Plain `JSON.stringify` would serialize a staged upload's byte array — megabytes of numbers — on
 * every render. The replacer collapses each `FileUploadRequest` to its name and length, which is
 * all the dirty check needs.
 */
export function themeFingerprint(theme: TenantThemeDto): string {
  const uploads = new Set(["logo", "logoDark", "favicon"]);
  return JSON.stringify(theme, (key, value) =>
    uploads.has(key) && value
      ? { fileName: (value as Schemas["FileUploadRequest"]).fileName, bytes: (value as Schemas["FileUploadRequest"]).data?.length ?? 0 }
      : value,
  );
}

/** Reset the caller's tenant theme to framework defaults. */
export async function resetTenantTheme(): Promise<void> {
  unwrapVoid(await api.POST("/api/v1/tenants/theme/reset", {}));
}
