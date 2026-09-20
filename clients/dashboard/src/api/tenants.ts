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
export type BrandAssetUploadsDto = Schemas["BrandAssetUploadsDto"];
export type TypographyDto = Schemas["TypographyDto"];
export type LayoutDto = Schemas["LayoutDto"];
export type TenantThemeDto = Schemas["TenantThemeDto"];

/**
 * The editors' working copy: the theme as the API returned it, plus whatever asset is staged for
 * THIS save.
 *
 * The two halves are separate on the wire since issue #83. The response's `brandAssets` carries the
 * URLs the server issued; the request's carries bytes and delete flags and **no URL at all** — there
 * is no field to paste a link into, which is the point: the column can only ever hold a value the
 * server produced for that asset. Carrying both on one draft object is a UI convenience;
 * `updateTenantTheme` is what separates them again.
 */
export type TenantThemeDraft = TenantThemeDto & {
  brandAssets: BrandAssetsDto & Partial<BrandAssetUploadsDto>;
};

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

/**
 * Save the caller's tenant theme. Needs MultitenancyPermissions.Tenants.UpdateTheme.
 *
 * Sends the write model only: a staged file per slot, or the flag that removes what is stored. The
 * draft's `logoUrl`/`logoDarkUrl`/`faviconUrl` are deliberately left behind — the server issues
 * those, and sending one back would be a client naming an asset URL (#83).
 */
export async function updateTenantTheme(draft: TenantThemeDraft): Promise<void> {
  const assets = draft.brandAssets;
  unwrapVoid(
    await api.PUT("/api/v1/tenants/theme", {
      body: {
        lightPalette: draft.lightPalette,
        darkPalette: draft.darkPalette,
        typography: draft.typography,
        layout: draft.layout,
        brandAssets: {
          logo: assets.logo ?? null,
          logoDark: assets.logoDark ?? null,
          favicon: assets.favicon ?? null,
          deleteLogo: assets.deleteLogo ?? false,
          deleteLogoDark: assets.deleteLogoDark ?? false,
          deleteFavicon: assets.deleteFavicon ?? false,
        },
      },
    }),
  );
}

/**
 * A comparable snapshot of a theme draft, for the editors' "unsaved" check.
 *
 * Plain `JSON.stringify` would serialize a staged upload's byte array — megabytes of numbers — on
 * every render. The replacer collapses each `FileUploadRequest` to its name and length, which is
 * all the dirty check needs.
 */
export function themeFingerprint(theme: TenantThemeDraft): string {
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
