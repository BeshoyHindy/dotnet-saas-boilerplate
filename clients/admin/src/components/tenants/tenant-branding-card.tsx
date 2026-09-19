import { Lock, Palette } from "lucide-react";
import { SettingsSection } from "@/components/list";

/**
 * TenantBrandingCard — operator-facing theme card for a single tenant.
 *
 * `getTenantTheme()` / `updateTenantTheme()` / `resetTenantTheme()` in
 * `@/api/tenants` are current-tenant-scoped server-side (see the comment
 * there) — they always act on the operator's own tenant. This card renders
 * on `/tenants/{id}`, i.e. someone else's tenant, so it must not fetch,
 * save, or reset: doing so would silently overwrite the operator's own
 * branding while looking like it edited `tenantId`'s. Disabled read-only
 * until the operator token-exchange endpoint lands.
 */
export function TenantBrandingCard({ tenantId }: { tenantId: string }) {
  void tenantId; // kept for API symmetry with the other per-tenant cards on this page

  return (
    <SettingsSection
      title="Branding"
      icon={Palette}
      description="Operator-controlled colors + brand assets that drive this tenant's UI."
    >
      <div
        role="status"
        aria-live="polite"
        className="flex items-start gap-3 rounded-xl border border-[var(--color-border)] bg-[var(--color-muted)] px-4 py-3"
      >
        <span
          aria-hidden
          className="grid h-7 w-7 shrink-0 place-items-center rounded-md bg-[var(--color-muted)] text-[var(--color-muted-foreground)]"
        >
          <Lock className="h-3.5 w-3.5" />
        </span>
        <p className="text-[13px] leading-relaxed text-[var(--color-muted-foreground)]">
          Editing another tenant's branding needs operator token exchange, which is not available yet.
        </p>
      </div>
    </SettingsSection>
  );
}
