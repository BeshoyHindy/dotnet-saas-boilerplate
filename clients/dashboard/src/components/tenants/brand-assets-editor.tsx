import { ImageInput, type ImageUpload } from "@/components/file/image-input";
import type { BrandAssetsDto } from "@/api/tenants";

/**
 * BrandAssetsEditor — logo / dark logo / favicon for a tenant theme, shared by the tenant-facing
 * Branding settings page and the operator's TenantBrandingCard so both offer the same one way.
 *
 * A picked file is staged on the draft as raw bytes (`logo` / `logoDark` / `favicon`) and uploaded
 * by the theme PUT itself: `TenantThemeService` writes it with `IStorageService.UploadAsync` into
 * the `uploads/` prefix and stores the durable unsigned URL. The Files module is deliberately not
 * involved — its `publicUrl` is a presigned GET that expires in minutes, so persisting one on the
 * theme row persists a dead link (issue #72). A pasted URL is still accepted for assets the tenant
 * hosts on its own CDN.
 */
export function BrandAssetsEditor({
  assets,
  onChange,
}: {
  assets: BrandAssetsDto;
  onChange: (next: Partial<BrandAssetsDto>) => void;
}) {
  return (
    <div className="overflow-hidden rounded-xl border border-[var(--color-border)] bg-[var(--color-surface-2)]">
      <div className="border-b border-[oklch(from_var(--color-border)_l_c_h_/_0.5)] px-4 py-2.5">
        <h4 className="text-[12.5px] font-semibold tracking-tight text-[var(--color-foreground)]">
          Brand assets
        </h4>
        <p className="mt-0.5 text-[11.5px] leading-relaxed text-[var(--color-muted-foreground)]">
          Upload an image, or paste a link to one you host elsewhere. Uploads are sent when you
          save this form.
        </p>
      </div>
      <div className="space-y-5 p-4">
        <AssetRow
          label="Logo"
          value={assets.logoUrl ?? ""}
          onUpload={(logo) => onChange({ logo, deleteLogo: false })}
          onUrl={(url) =>
            onChange(
              url
                ? { logoUrl: url, logo: null, deleteLogo: false }
                : { logoUrl: null, logo: null, deleteLogo: true },
            )
          }
        />
        <AssetRow
          label="Logo (dark mode)"
          value={assets.logoDarkUrl ?? ""}
          onUpload={(logoDark) => onChange({ logoDark, deleteLogoDark: false })}
          onUrl={(url) =>
            onChange(
              url
                ? { logoDarkUrl: url, logoDark: null, deleteLogoDark: false }
                : { logoDarkUrl: null, logoDark: null, deleteLogoDark: true },
            )
          }
        />
        <AssetRow
          label="Favicon"
          value={assets.faviconUrl ?? ""}
          onUpload={(favicon) => onChange({ favicon, deleteFavicon: false })}
          onUrl={(url) =>
            onChange(
              url
                ? { faviconUrl: url, favicon: null, deleteFavicon: false }
                : { faviconUrl: null, favicon: null, deleteFavicon: true },
            )
          }
        />
      </div>
    </div>
  );
}

function AssetRow({
  label,
  value,
  onUpload,
  onUrl,
}: {
  label: string;
  value: string;
  onUpload: (image: ImageUpload) => void;
  onUrl: (next: string) => void;
}) {
  return (
    <div>
      <div className="mb-1.5 text-[10px] font-semibold uppercase tracking-[0.12em] text-[var(--color-muted-foreground)]">
        {label}
      </div>
      <ImageInput value={value} onUpload={onUpload} onChange={onUrl} />
    </div>
  );
}
