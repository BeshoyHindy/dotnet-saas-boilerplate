import { describe, expect, it } from "vitest";
import { matchRoutes, type RouteObject } from "react-router-dom";
import { isValidElement } from "react";
import { router } from "@/routes";
import { RouteGuard } from "@/auth/route-guard";
import { sections, topNavBottom, topNavTop, type NavSpec } from "@/components/layout/nav-data";

/**
 * `.agents/rules/frontend/clients.md`: "Gate a *route* with `<RouteGuard perms={[…]}>`",
 * mirroring the permission its nav item gates on. This asserts the two never drift —
 * a nav item that hides a link is not a security boundary if the route behind it isn't
 * guarded with the same permission. A route's detail child (e.g. `identity/users/:userId`
 * under `/identity/users`) is only reachable from that list page, so it must carry the
 * same guard even though it has no nav entry of its own.
 */

/** The RouteGuard element wrapping the route registered for `to`, if any. */
function guardFor(to: string) {
  const matches = matchRoutes(router.routes, to);
  const leaf = matches?.at(-1)?.route.element;
  return isValidElement(leaf) && leaf.type === RouteGuard
    ? (leaf.props as { perms?: readonly string[]; anyPerms?: readonly string[] })
    : null;
}

/** Every absolute path registered anywhere in the route tree (routes with no `path`,
 *  e.g. layout/index routes, are skipped — only real addresses are collected). */
function collectPaths(routes: RouteObject[], base = ""): string[] {
  return routes.flatMap((route) => {
    const current = route.path
      ? `${base}/${route.path}`.replace(/\/{2,}/g, "/")
      : base;
    const own = route.path ? [current] : [];
    const children = route.children ? collectPaths(route.children, current) : [];
    return [...own, ...children];
  });
}

const allPaths = collectPaths(router.routes);

const gatedItems: NavSpec[] = [
  ...topNavTop,
  ...topNavBottom,
  ...sections.flatMap((s) => s.items),
].filter((item) => item.perm || item.anyPerm);

describe("nav item guards", () => {
  it.each(gatedItems)("$to is wrapped in a RouteGuard matching its nav gate", (item) => {
    const guard = guardFor(item.to);
    expect(guard).not.toBeNull();
    expect(guard?.perms ?? []).toEqual(item.perm ? [item.perm] : []);
    expect(guard?.anyPerms).toEqual(item.anyPerm);
  });

  it("treats an ungated nav item as unguarded", () => {
    // Guards the guard: Health has neither `perm` nor `anyPerm` and no RouteGuard.
    expect(guardFor("/system/health")).toBeNull();
  });

  describe("detail routes under a guarded list page", () => {
    for (const item of gatedItems) {
      const children = allPaths.filter(
        (path) => path !== item.to && path.startsWith(`${item.to}/`),
      );
      for (const childPath of children) {
        it(`${childPath} carries ${item.to}'s guard`, () => {
          const guard = guardFor(childPath);
          expect(guard).not.toBeNull();
          expect(guard?.perms ?? []).toEqual(item.perm ? [item.perm] : []);
          expect(guard?.anyPerms).toEqual(item.anyPerm);
        });
      }
    }
  });
});
