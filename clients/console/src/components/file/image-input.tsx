import { useEffect, useRef, useState } from "react";
import { Image as ImageIcon, Loader2, Upload, X } from "lucide-react";
import { toast } from "sonner";
import { Button } from "@/components/ui/button";
import { cn } from "@/lib/cn";
import { formatBytes } from "@/hooks/use-file-upload";
import type { Schemas } from "@/lib/api-client";

/** The wire shape every module endpoint that accepts an image takes (`FileUploadRequest`). */
export type ImageUpload = Schemas["FileUploadRequest"];

type Props = {
  /** Current image URL (or empty). The component is fully controlled. */
  value: string;
  /**
   * A picked image, as raw bytes for the OWNING module's endpoint (profile PUT, tenant theme
   * PUT, …). Those write through `IStorageService.UploadAsync` into the `uploads/` prefix — the
   * one key space the deploy stacks publish — and hand back a durable, unsigned URL.
   */
  onUpload: (image: ImageUpload) => void;
  /** The user cleared the image: the caller removes what is stored. */
  onRemove: () => void;
  /** The caller's save is in flight. */
  busy?: boolean;
  /** Allowed extensions (lower-case w/ leading dot). Server enforces the same list. */
  allowedExtensions?: string[];
  maxBytes?: number;
  /** Visual treatment for the preview tile — "square" for logos, "circle" for avatars. */
  shape?: "square" | "circle";
  className?: string;
};

/**
 * `FileTypeMetadata.GetRules(FileType.Image)` server-side. Anything else is rejected there, so
 * offering it here would only produce a 400 after the bytes have been sent.
 */
const IMAGE_EXTS = [".jpg", ".jpeg", ".png", ".ico"];

/**
 * The transport is a JSON array of bytes (`List<byte>`), which inflates ~4x, and Kestrel caps a
 * request body at 10 MB (`RequestLimitsOptions`). 2 MB of image is a generous avatar/logo and
 * leaves room under that ceiling; the server's own 5 MB rule is the backstop.
 */
const MAX_BYTES = 2 * 1024 * 1024;

/**
 * ImageInput — pick an image, uploaded as bytes through the owning module's own endpoint, or remove
 * the one that is stored. There is no "paste a URL" mode any more (#83): the API stopped accepting
 * an asset URL from a client, because a URL a client names may be an object belonging to someone
 * else in the same tenant, and replacing or removing the asset would then delete *their* bytes. What
 * the field shows is the URL the server issued.
 *
 * It does NOT go through the Files module. A Files `publicUrl` is a presigned GET that expires in
 * minutes (see `.agents/rules/modules/files.md`), so persisting one on an entity column — an
 * avatar, a tenant logo — stores a link that is dead by the time anyone loads the page (issue #72).
 */
export function ImageInput({
  value,
  onUpload,
  onRemove,
  busy = false,
  allowedExtensions = IMAGE_EXTS,
  maxBytes = MAX_BYTES,
  shape = "square",
  className,
}: Props) {
  const [reading, setReading] = useState(false);

  // Local preview of the just-picked file, shown until the caller's save round-trips a real URL.
  // It is tagged with the `value` it was taken against, so a new `value` retires it by derivation
  // rather than through a reset effect.
  const [preview, setPreview] = useState<{ url: string; forValue: string } | null>(null);
  const previewRef = useRef<string | null>(null);
  const setPreviewUrl = (next: { url: string; forValue: string } | null) => {
    if (previewRef.current) URL.revokeObjectURL(previewRef.current);
    previewRef.current = next?.url ?? null;
    setPreview(next);
  };
  useEffect(() => () => setPreviewUrl(null), []);

  // Likewise for a load failure: remember which src failed instead of resetting a flag on change.
  const [failedSrc, setFailedSrc] = useState<string | null>(null);

  const handlePick = () => {
    const input = document.createElement("input");
    input.type = "file";
    input.accept = allowedExtensions.join(",");
    input.onchange = async () => {
      const file = input.files?.[0];
      if (!file) return;

      const dot = file.name.lastIndexOf(".");
      const ext = dot > 0 ? file.name.slice(dot).toLowerCase() : "";
      if (!allowedExtensions.includes(ext)) {
        toast.error(`"${ext || file.name}" is not an accepted image type`, {
          description: `Use ${allowedExtensions.join(", ")}.`,
        });
        return;
      }
      if (file.size > maxBytes) {
        toast.error("Image is too large", {
          description: `${formatBytes(file.size)} — the limit is ${formatBytes(maxBytes)}.`,
        });
        return;
      }

      setReading(true);
      try {
        const bytes = new Uint8Array(await file.arrayBuffer());
        setPreviewUrl({ url: URL.createObjectURL(file), forValue: value });
        onUpload({
          fileName: file.name,
          contentType: file.type || "application/octet-stream",
          // List<byte> on the wire: a plain array of numbers, not base64.
          data: Array.from(bytes),
        });
      } catch {
        toast.error("Could not read that file");
      } finally {
        setReading(false);
      }
    };
    input.click();
  };

  // The picked file wins until the caller's `value` changes; then the fresh URL takes over.
  const src = preview && preview.forValue === value ? preview.url : value;
  const isWorking = busy || reading;
  // Show the placeholder (not a broken-image icon) when the current src fails to load — e.g. a
  // seeded default-avatar URL that 404s.
  const showImage = src.length > 0 && failedSrc !== src;
  const tileClass = shape === "circle" ? "rounded-full" : "rounded-xl";

  const clear = () => {
    setPreviewUrl(null);
    onRemove();
  };

  return (
    <div className={cn("space-y-3", className)}>
      {/* Preview + controls row */}
      <div className="flex items-start gap-4">
        <div
          className={cn(
            "relative grid place-items-center overflow-hidden bg-[var(--color-muted)] ring-1 ring-inset ring-border",
            shape === "circle" ? "h-20 w-20 rounded-full" : "h-24 w-24 rounded-xl",
          )}
        >
          {showImage ? (
            <img
              src={src}
              alt=""
              onError={() => setFailedSrc(src)}
              className={cn("h-full w-full object-cover", tileClass)}
            />
          ) : isWorking ? (
            <Loader2 className="h-5 w-5 animate-spin text-[var(--color-primary)]" />
          ) : (
            <ImageIcon className="h-5 w-5 text-[var(--color-muted-foreground)]" />
          )}
        </div>

        <div className="flex-1 space-y-2">
          <div className="flex flex-wrap items-center gap-2">
            <Button type="button" size="sm" onClick={handlePick} disabled={isWorking}>
              {isWorking
                ? <Loader2 className="h-3.5 w-3.5 animate-spin" />
                : <Upload className="h-3.5 w-3.5" />}
              {showImage ? "Replace image" : "Choose image"}
            </Button>
            {showImage && !isWorking && (
              <Button type="button" size="sm" variant="outline" onClick={clear}>
                <X className="h-3.5 w-3.5" />
                Remove
              </Button>
            )}
          </div>

          <p className="text-xs text-[var(--color-muted-foreground)]">
            {allowedExtensions.join(" / ")} · up to {formatBytes(maxBytes)}
          </p>
        </div>
      </div>
    </div>
  );
}
