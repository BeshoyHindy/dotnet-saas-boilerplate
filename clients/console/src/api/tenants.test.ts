import { beforeEach, describe, expect, it, vi } from "vitest";
import {
  DEFAULT_DARK_PALETTE,
  DEFAULT_LIGHT_PALETTE,
  themeFingerprint,
  updateTenantTheme,
  type TenantThemeDraft,
} from "@/api/tenants";

// The real client builds a Request from a relative URL, which undici (jsdom's fetch) refuses; the
// question here is what `updateTenantTheme` puts in the body, so the transport is stubbed out.
const { put } = vi.hoisted(() => ({ put: vi.fn() }));
vi.mock("@/lib/api-client", () => ({
  api: { PUT: put },
  unwrap: (r: { data: unknown }) => r.data,
  unwrapVoid: () => undefined,
}));

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

/**
 * #83: the save sends the theme's WRITE model, which carries bytes and delete flags and no URL.
 * The draft the editor holds does carry the URLs the server issued — it shows them — so the one
 * place the two halves are separated again is here, and this is what pins it. A client that put a
 * URL back on the wire would be naming an object it may not own: inside one tenant that could be
 * another user's avatar, which the next replace would delete.
 */
describe("updateTenantTheme", () => {
  const sent = () => put.mock.calls[0][1].body as Record<string, unknown>;

  beforeEach(() => {
    put.mockReset();
    put.mockResolvedValue({ data: undefined, error: undefined, response: { status: 204 } });
  });

  it("puts to the theme route and sends no asset URL at all", async () => {
    await updateTenantTheme(
      theme({
        brandAssets: assets({
          logoUrl: "https://cdn.example.com/uploads/tenants/acme/appuser/someone-else/theirs.png",
          logoDarkUrl: "https://cdn.example.com/dark.png",
          faviconUrl: "https://cdn.example.com/fav.ico",
        }),
      }),
    );

    expect(put).toHaveBeenCalledTimes(1);
    expect(put.mock.calls[0][0]).toBe("/api/v1/tenants/theme");
    expect(JSON.stringify(sent())).not.toContain("cdn.example.com");
    expect(Object.keys(sent().brandAssets as object).sort()).toEqual([
      "deleteFavicon",
      "deleteLogo",
      "deleteLogoDark",
      "favicon",
      "logo",
      "logoDark",
    ]);
  });

  it("forwards a staged upload for the slot it was staged on", async () => {
    const logo = { fileName: "logo.png", contentType: "image/png", data: [1, 2, 3] };

    await updateTenantTheme(theme({ brandAssets: assets({ logo }) }));

    expect(sent().brandAssets).toMatchObject({ logo, logoDark: null, favicon: null });
  });

  it("forwards a removal as its flag, not as an empty URL", async () => {
    await updateTenantTheme(theme({ brandAssets: assets({ logoUrl: null, deleteLogo: true }) }));

    expect(sent().brandAssets).toMatchObject({
      logo: null,
      deleteLogo: true,
      deleteLogoDark: false,
      deleteFavicon: false,
    });
  });
});
