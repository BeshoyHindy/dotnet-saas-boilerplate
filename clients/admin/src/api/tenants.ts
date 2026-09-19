import { apiFetch } from "@/lib/api-client";
import type { PagedResponse } from "@/lib/api-types";

export type { PagedResponse } from "@/lib/api-types";

export type TenantExpiryState = "Active" | "InGrace" | "Expired" | (string & {});

export type TenantDto = {
  id: string;
  name: string;
  adminEmail: string;
  isActive: boolean;
  validUpto: string;
  issuer?: string;
  expiryState?: TenantExpiryState;
  graceEndsUtc?: string;
};

export type ListTenantsParams = {
  pageNumber?: number;
  pageSize?: number;
  sort?: string;
};

export type CreateTenantInput = {
  id: string;
  name: string;
  adminEmail: string;
  adminPassword: string;
  issuer: string;
  connectionString?: string | null;
  /** ISO date-time the tenant stays valid until. Omitted → server default. */
  validUpto?: string | null;
};

export type RenewTenantResponse = {
  tenantId: string;
  validUpto: string;
};

export type AdjustTenantValidityResponse = {
  tenantId: string;
  validUpto: string;
};

export type CreateTenantResponse = {
  id: string;
  provisioningCorrelationId?: string;
  status?: string;
};

export type TenantLifecycleResult = {
  tenantId: string;
  isActive: boolean;
};

export type TenantProvisioningStep = {
  step: string;
  status: string;
  startedUtc?: string | null;
  completedUtc?: string | null;
  error?: string | null;
};

export type TenantProvisioningStatus = {
  tenantId: string;
  status: string;
  correlationId: string;
  currentStep?: string | null;
  error?: string | null;
  createdUtc: string;
  startedUtc?: string | null;
  completedUtc?: string | null;
  steps: TenantProvisioningStep[];
};

export async function listTenants(params: ListTenantsParams = {}): Promise<PagedResponse<TenantDto>> {
  const query = new URLSearchParams();
  query.set("PageNumber", String(params.pageNumber ?? 1));
  query.set("PageSize", String(params.pageSize ?? 10));
  if (params.sort) query.set("Sort", params.sort);
  return apiFetch<PagedResponse<TenantDto>>(`/api/v1/tenants/?${query.toString()}`);
}

export async function getTenantStatus(id: string): Promise<TenantDto> {
  return apiFetch<TenantDto>(`/api/v1/tenants/${encodeURIComponent(id)}/status`);
}

export async function getTenantProvisioningStatus(id: string): Promise<TenantProvisioningStatus> {
  return apiFetch<TenantProvisioningStatus>(`/api/v1/tenants/${encodeURIComponent(id)}/provisioning`);
}

export async function createTenant(input: CreateTenantInput): Promise<CreateTenantResponse> {
  return apiFetch<CreateTenantResponse>(`/api/v1/tenants/`, {
    method: "POST",
    body: JSON.stringify({
      id: input.id,
      name: input.name,
      adminEmail: input.adminEmail,
      adminPassword: input.adminPassword,
      issuer: input.issuer,
      connectionString: input.connectionString ?? null,
      validUpto: input.validUpto ?? null,
    }),
  });
}

/** Renew a tenant, optionally extending validity by a specific number of months (1–120). Omitted → server default. */
export async function renewTenant(id: string, months?: number | null): Promise<RenewTenantResponse> {
  return apiFetch<RenewTenantResponse>(`/api/v1/tenants/${encodeURIComponent(id)}/renew`, {
    method: "POST",
    body: JSON.stringify({ months: months ?? null }),
  });
}

/**
 * Operator override: set a tenant's ValidUpto directly (comp/correction).
 * Backdating is allowed server-side. Root-operator only — gated by
 * MultitenancyPermissions.Tenants.UpgradeSubscription, same as renew.
 */
export async function adjustTenantValidity(id: string, validUpto: string): Promise<AdjustTenantValidityResponse> {
  return apiFetch<AdjustTenantValidityResponse>(`/api/v1/tenants/${encodeURIComponent(id)}/adjust-validity`, {
    method: "POST",
    body: JSON.stringify({ tenantId: id, validUpto }),
  });
}

export async function changeTenantActivation(id: string, isActive: boolean): Promise<TenantLifecycleResult> {
  return apiFetch<TenantLifecycleResult>(`/api/v1/tenants/${encodeURIComponent(id)}/activation`, {
    method: "POST",
    body: JSON.stringify({ tenantId: id, isActive }),
  });
}

export async function retryTenantProvisioning(id: string): Promise<TenantProvisioningStatus> {
  return apiFetch<TenantProvisioningStatus>(`/api/v1/tenants/${encodeURIComponent(id)}/provisioning/retry`, {
    method: "POST",
  });
}

// ─────────────────────────────────────────────────────────────────────────
// Tenant theme / branding
//
// The theme endpoints are CURRENT-TENANT scoped server-side: they act on the
// tenant the caller's token names, and a caller cannot name another one
// (ADR-0002). To edit tenant X's branding, ENTER tenant X first (#9's operator
// token exchange): the acting token makes X the current tenant, so these same
// calls read and write X. TenantBrandingCard gates on exactly that.
// ─────────────────────────────────────────────────────────────────────────

export type PaletteDto = {
  primary: string;
  secondary: string;
  tertiary: string;
  background: string;
  surface: string;
  error: string;
  warning: string;
  success: string;
  info: string;
};

export type BrandAssetsDto = {
  logoUrl?: string | null;
  logoDarkUrl?: string | null;
  faviconUrl?: string | null;
  deleteLogo?: boolean;
  deleteLogoDark?: boolean;
  deleteFavicon?: boolean;
};

export type TypographyDto = {
  fontFamily: string;
  headingFontFamily: string;
  fontSizeBase: number;
  lineHeightBase: number;
};

export type LayoutDto = {
  borderRadius: string;
  defaultElevation: number;
};

export type TenantThemeDto = {
  lightPalette: PaletteDto;
  darkPalette: PaletteDto;
  brandAssets: BrandAssetsDto;
  typography: TypographyDto;
  layout: LayoutDto;
  isDefault: boolean;
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
  return apiFetch<TenantThemeDto>(`/api/v1/tenants/theme`);
}

/** Save the caller's tenant theme. Needs MultitenancyPermissions.Tenants.UpdateTheme. */
export async function updateTenantTheme(theme: TenantThemeDto): Promise<void> {
  await apiFetch<void>(`/api/v1/tenants/theme`, {
    method: "PUT",
    body: JSON.stringify(theme),
  });
}

/** Reset the caller's tenant theme to framework defaults. */
export async function resetTenantTheme(): Promise<void> {
  await apiFetch<void>(`/api/v1/tenants/theme/reset`, {
    method: "POST",
  });
}
