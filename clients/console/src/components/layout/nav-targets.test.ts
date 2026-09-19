import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { describe, expect, it } from "vitest";
import { matchRoutes } from "react-router-dom";
import { router } from "@/routes";
import { sections, topNavBottom, topNavTop } from "@/components/layout/nav-data";
import { navigationGroups } from "@/components/command-palette/palette-actions";

/**
 * Every place the chrome can send someone must land on a route this app registers.
 *
 * The console inherited its nav from two retired apps, and three entries pointed at
 * pages that never came across — `/settings/api-keys` (topbar menu + palette) and
 * `/settings/notifications` (palette). They rendered the 404 page. Nothing else
 * would have caught that: the targets are strings, not links React Router validates.
 */

/** True when `path` is answered by a real route rather than the `*` catch-all. */
function resolvesToRegisteredRoute(path: string): boolean {
  const pathname = path.split("?")[0];
  // `router.routes` is the table createBrowserRouter was built from — matching against
  // it needs no history, so this stays a plain unit test.
  const matches = matchRoutes(router.routes, pathname);
  if (!matches || matches.length === 0) return false;
  return !matches.some((match) => match.route.path === "*");
}

describe("nav targets", () => {
  const sidebarTargets = [
    ...topNavTop.map((item) => item.to),
    ...topNavBottom.map((item) => item.to),
    ...sections.flatMap((section) => section.items.map((item) => item.to)),
  ];

  it.each(sidebarTargets)("sidebar entry %s resolves to a route", (target) => {
    expect(resolvesToRegisteredRoute(target)).toBe(true);
  });

  const paletteTargets = navigationGroups.flatMap((group) => group.items.map((item) => item.to));

  it.each(paletteTargets)("command-palette entry %s resolves to a route", (target) => {
    expect(resolvesToRegisteredRoute(target)).toBe(true);
  });

  // The topbar's account menu navigates from inline handlers, so there is no array to
  // walk — read the literals straight out of the source instead. Crude, but it is the
  // file the dead "API keys" item lived in, and it stays honest without a refactor.
  // Vitest runs with the package root as cwd (see `pnpm test`).
  const topbarSource = readFileSync(
    resolve(process.cwd(), "src/components/layout/topbar.tsx"),
    "utf8",
  );
  const topbarTargets = [...topbarSource.matchAll(/navigate\("(\/[^"]*)"/g)].map((m) => m[1]);

  it("finds the topbar's navigation targets", () => {
    expect(topbarTargets.length).toBeGreaterThan(0);
  });

  it.each(topbarTargets)("topbar entry %s resolves to a route", (target) => {
    expect(resolvesToRegisteredRoute(target)).toBe(true);
  });

  it("treats an unregistered path as unresolved", () => {
    // Guards the guard: without this, a matcher that says yes to everything would
    // make every assertion above vacuous.
    expect(resolvesToRegisteredRoute("/settings/api-keys")).toBe(false);
  });
});
