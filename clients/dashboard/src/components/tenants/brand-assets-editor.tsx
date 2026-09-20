import { ImageInput, type ImageUpload } from "@/components/file/image-input";
import type { TenantThemeDraft } from "@/api/tenants";

type DraftAssets = TenantThemeDraft["brandAssets"];

/**
 * BrandAssetsEditor — logo / dark logo / favicon for a tenant theme, shared by the tenant-facing
 * Branding settings page and the operator's TenantBrandingCard so both offer the same one way.
 *
 * A picked file is staged on the draft as raw bytes (`logo` / `logoDark` / `favicon`) and uploaded
 * by the theme PUT itself: `TenantThemeService` writes it with `IStorageService.UploadAsync` into
 * the `uploads/` prefix, under that asset's own owner segment, and stores the durable unsigned URL.
 * The Files module is deliberately not involved — its `publicUrl` is a presigned GET that expires in
 * minutes, so persisting one on the theme row persists a dead link (issue #72).
 *
 * **A URL cannot be typed in here, and the API would not take one (#83).** What is shown is the URL
 * the server issued; removing an asset is a flag, so the object deleted is always the one this slot
 * uploaded rather than whatever address happened to be sitting in the column.
 *
 * A removal also blanks that slot's `…Url` **on the draft**, so the preview empties as soon as the
 * user clicks Remove instead of lingering until the save round-trips. That is a local edit to the
 * editor's copy of the read model; `updateTenantTheme` sends the write model, which has no URL field
 * to put it in.
 */
export function BrandAssetsEditor({
  assets,
  onChange,
}: {
  assets: DraftAssets;
  onChange: (next: Partial<DraftAssets>) => void;
}) {
  return (
    <div className="overflow-hidden rounded-xl border border-[var(--color-border)] bg-[var(--color-surface-2)]">
      <div className="border-b border-[oklch(from_var(--color-border)_l_c_h_/_0.5)] px-4 py-2.5">
        <h4 className="text-[12.5px] font-semibold tracking-tight text-[var(--color-foreground)]">
          Brand assets
        </h4>
        <p className="mt-0.5 text-[11.5px] leading-relaxed text-[var(--color-muted-foreground)]">
          Upload an image for each slot. Uploads and removals are sent when you save this form.
        </p>
      </div>
      <div className="space-y-5 p-4">
        <AssetRow
          label="Logo"
          value={assets.logoUrl ?? ""}
          onUpload={(logo) => onChange({ logo, deleteLogo: false })}
          onRemove={() => onChange({ logoUrl: null, logo: null, deleteLogo: true })}
        />
        <AssetRow
          label="Logo (dark mode)"
          value={assets.logoDarkUrl ?? ""}
          onUpload={(logoDark) => onChange({ logoDark, deleteLogoDark: false })}
          onRemove={() => onChange({ logoDarkUrl: null, logoDark: null, deleteLogoDark: true })}
        />
        <AssetRow
          label="Favicon"
          value={assets.faviconUrl ?? ""}
          onUpload={(favicon) => onChange({ favicon, deleteFavicon: false })}
          onRemove={() => onChange({ faviconUrl: null, favicon: null, deleteFavicon: true })}
        />
      </div>
    </div>
  );
}

function AssetRow({
  label,
  value,
  onUpload,
  onRemove,
}: {
  label: string;
  value: string;
  onUpload: (image: ImageUpload) => void;
  onRemove: () => void;
}) {
  return (
    <div>
      <div className="mb-1.5 text-[10px] font-semibold uppercase tracking-[0.12em] text-[var(--color-muted-foreground)]">
        {label}
      </div>
      <ImageInput value={value} onUpload={onUpload} onRemove={onRemove} />
    </div>
  );
}
