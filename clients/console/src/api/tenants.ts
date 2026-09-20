import { api, unwrap, unwrapVoid, type Paged, type Schemas } from "@/lib/api-client";

// ─────────────────────────────────────────────────────────────────────────
// Tenant status / validity
//
// Drives the global expiry/grace banner, the overview page's "Valid for" stat
// and the operator tenant screens — the tenant's validity window, not a
// subscription plan.
// ─────────────────────────────────────────────────────────────────────────

export type TenantExpiryState = "Active" | "InGrace" | "Expired" | (string & {});

export type TenantDto = Schemas["TenantDto"];
export type TenantStatusDto = Schemas["TenantStatusDto"];
export type TenantLifecycleResult = Schemas["TenantLifecycleResultDto"];
export type TenantProvisioningStatus = Schemas["TenantProvisioningStatusDto"];
export type TenantProvisioningStep = Schemas["TenantProvisioningStepDto"];
export type CreateTenantResponse = Schemas["CreateTenantCommandResponse"];
export type RenewTenantResponse = Schemas["RenewTenantCommandResponse"];
export type AdjustTenantValidityResponse = Schemas["AdjustTenantValidityCommandResponse"];
export type CreateTenantInput = Schemas["CreateTenantCommand"];

/** Fetch the current tenant's validity status. */
export async function getMyStatus(): Promise<TenantStatusDto> {
  return unwrap(await api.GET("/api/v1/tenants/me/status", {}));
}

export type ListTenantsParams = {
  pageNumber?: number;
  pageSize?: number;
  sort?: string;
};

export async function listTenants(params: ListTenantsParams = {}): Promise<Paged<TenantDto>> {
  return unwrap(
    await api.GET("/api/v1/tenants", {
      params: {
        query: {
          PageNumber: params.pageNumber ?? 1,
          PageSize: params.pageSize ?? 10,
          Sort: params.sort,
        },
      },
    }),
  );
}

export async function getTenantStatus(id: string): Promise<TenantStatusDto> {
  return unwrap(await api.GET("/api/v1/tenants/{id}/status", { params: { path: { id } } }));
}

export async function getTenantProvisioningStatus(id: string): Promise<TenantProvisioningStatus> {
  return unwrap(
    await api.GET("/api/v1/tenants/{tenantId}/provisioning", {
      params: { path: { tenantId: id } },
    }),
  );
}

export async function createTenant(input: CreateTenantInput): Promise<CreateTenantResponse> {
  return unwrap(await api.POST("/api/v1/tenants", { body: input }));
}

/**
 * Renew a tenant, optionally extending validity by a specific number of months
 * (1–120). Omitted → server default.
 */
export async function renewTenant(id: string, months?: number | null): Promise<RenewTenantResponse> {
  return unwrap(
    await api.POST("/api/v1/tenants/{id}/renew", {
      params: { path: { id } },
      body: { tenantId: id, months: months ?? null },
    }),
  );
}

/**
 * Operator override: set a tenant's ValidUpto directly (comp/correction).
 * Backdating is allowed server-side. Root-operator only — gated by
 * MultitenancyPermissions.Tenants.UpgradeSubscription, same as renew.
 */
export async function adjustTenantValidity(
  id: string,
  validUpto: string,
): Promise<AdjustTenantValidityResponse> {
  return unwrap(
    await api.POST("/api/v1/tenants/{id}/adjust-validity", {
      params: { path: { id } },
      body: { tenantId: id, validUpto },
    }),
  );
}

export async function changeTenantActivation(
  id: string,
  isActive: boolean,
): Promise<TenantLifecycleResult> {
  return unwrap(
    await api.POST("/api/v1/tenants/{id}/activation", {
      params: { path: { id } },
      body: { tenantId: id, isActive },
    }),
  );
}

export async function retryTenantProvisioning(id: string): Promise<TenantProvisioningStatus> {
  return unwrap(
    await api.POST("/api/v1/tenants/{tenantId}/provisioning/retry", {
      params: { path: { tenantId: id } },
    }),
  );
}

// ─────────────────────────────────────────────────────────────────────────
// Tenant theme / branding
//
// The theme endpoints are CURRENT-TENANT scoped server-side: they act on the tenant the
// caller's token names, and a caller cannot name another one (ADR-0002). To edit tenant
// X's branding, ENTER tenant X first (the operator token exchange, #9): the acting token
// makes X the current tenant, so these same calls read and write X. TenantBrandingCard
// gates on exactly that.
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
