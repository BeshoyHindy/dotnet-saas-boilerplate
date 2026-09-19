import { describe, expect, it } from "vitest";
import {
  DEFAULT_DARK_PALETTE,
  DEFAULT_LIGHT_PALETTE,
  themeFingerprint,
  type TenantThemeDraft,
} from "@/api/tenants";

type DraftAssets = TenantThemeDraft["brandAssets"];

/**
 * The draft's asset half: the URLs the server issued, plus whatever is staged for this save. The
 * URLs are read-only as far as the API is concerned (#83) — they are here because the editor shows
 * them, not because a save sends them.
 */
function assets(overrides: Partial<DraftAssets> = {}): DraftAssets {
  return {
    logoUrl: null,
    logoDarkUrl: null,
    faviconUrl: null,
    logo: null,
    logoDark: null,
    favicon: null,
    deleteLogo: false,
    deleteLogoDark: false,
    deleteFavicon: false,
    ...overrides,
  };
}

function theme(overrides: Partial<TenantThemeDraft> = {}): TenantThemeDraft {
  return {
    lightPalette: DEFAULT_LIGHT_PALETTE,
    darkPalette: DEFAULT_DARK_PALETTE,
    brandAssets: assets(),
    typography: { fontFamily: "Figtree", headingFontFamily: "Outfit", fontSizeBase: 16, lineHeightBase: 1.5 },
    layout: { borderRadius: 12, defaultElevation: 1 },
    isDefault: false,
    ...overrides,
  } as TenantThemeDraft;
}

describe("themeFingerprint", () => {
  it("treats an unchanged draft as unchanged", () => {
    expect(themeFingerprint(theme())).toBe(themeFingerprint(theme()));
  });

  it("sees a palette edit", () => {
    const edited = theme({ lightPalette: { ...DEFAULT_LIGHT_PALETTE, primary: "#000000" } });
    expect(themeFingerprint(edited)).not.toBe(themeFingerprint(theme()));
  });

  it("sees a staged brand-asset upload, so Save enables", () => {
    const staged = theme({
      brandAssets: assets({ logo: { fileName: "logo.png", contentType: "image/png", data: [1, 2, 3] } }),
    });
    expect(themeFingerprint(staged)).not.toBe(themeFingerprint(theme()));
  });

  it("collapses the staged bytes instead of serializing them", () => {
    // A megabyte-scale byte array is re-serialized on every render of the editor; the fingerprint
    // must summarize it rather than stringify it.
    const big = Array.from({ length: 50_000 }, (_, i) => i % 256);
    const staged = theme({
      brandAssets: assets({ logo: { fileName: "logo.png", contentType: "image/png", data: big } }),
    });

    const fingerprint = themeFingerprint(staged);
    expect(fingerprint.length).toBeLessThan(2_000);
    expect(fingerprint).toContain("logo.png");
    expect(fingerprint).toContain("50000");
  });

  it("distinguishes two different staged files of the same length", () => {
    const withFile = (fileName: string) =>
      themeFingerprint(
        theme({
          brandAssets: assets({ logo: { fileName, contentType: "image/png", data: [1, 2, 3] } }),
        }),
      );

    expect(withFile("a.png")).not.toBe(withFile("b.png"));
  });
});
