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
// The theme endpoints are CURRENT-TENANT scoped server-side: they act on the
// tenant the caller's token names (ADR-0002). An operator therefore edits another
// tenant's branding only from inside that tenant — after entering it through the
// impersonation/token exchange.
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

/** Reset the caller's tenant theme to framework defaults. */
export async function resetTenantTheme(): Promise<void> {
  unwrapVoid(await api.POST("/api/v1/tenants/theme/reset", {}));
}
