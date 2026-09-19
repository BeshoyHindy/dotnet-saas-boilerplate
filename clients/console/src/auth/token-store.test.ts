import { beforeEach, describe, expect, it } from "vitest";
import { tokenStore } from "@/auth/token-store";

beforeEach(() => {
  localStorage.clear();
});

describe("tokenStore", () => {
  it("namespaces its keys under boilerplate.console", () => {
    tokenStore.setAccessToken("access-1");
    tokenStore.setTenant("acme");

    expect(localStorage.getItem("boilerplate.console.accessToken")).toBe("access-1");
    expect(localStorage.getItem("boilerplate.console.tenant")).toBe("acme");
  });

  it("never stores a refresh token — that lives in an HttpOnly cookie (ADR-0002)", () => {
    tokenStore.setAccessToken("access-1");
    tokenStore.setTenant("acme");
    tokenStore.setPermissions(["Permissions.Users.View"]);

    expect(Object.keys(localStorage).some((key) => /refresh/i.test(key))).toBe(false);
  });

  it("round-trips the permission set and survives a corrupt entry", () => {
    tokenStore.setPermissions(["Permissions.Users.View", "Permissions.Roles.View"]);
    expect(tokenStore.getPermissions()).toEqual([
      "Permissions.Users.View",
      "Permissions.Roles.View",
    ]);

    localStorage.setItem("boilerplate.console.permissions", "{not json");
    expect(tokenStore.getPermissions()).toEqual([]);
  });

  it("keeps no acting credential — that never touches storage (ADR-0002)", () => {
    tokenStore.setAccessToken("operator-token");
    tokenStore.setTenant("root");
    tokenStore.setPermissions(["Permissions.Tenants.View"]);

    // Acting as someone else swaps nothing here: the acting token lives in module
    // memory (acting-store), so the operator's own session is the only one on disk.
    // The old impersonation stash is gone, and so is any way to resurrect a dead
    // acting token after a reload.
    expect(Object.keys(localStorage)).toEqual(
      expect.arrayContaining([
        "boilerplate.console.accessToken",
        "boilerplate.console.tenant",
        "boilerplate.console.permissions",
      ]),
    );
    expect(Object.keys(localStorage)).toHaveLength(3);
    expect(Object.keys(localStorage).some((key) => /impersonat|acting|actor/i.test(key))).toBe(
      false,
    );
  });

  it("clear() drops the whole session", () => {
    tokenStore.setAccessToken("operator-token");
    tokenStore.setTenant("root");
    tokenStore.setPermissions(["Permissions.Tenants.View"]);

    tokenStore.clear();

    expect(tokenStore.getAccessToken()).toBeNull();
    expect(tokenStore.getPermissions()).toEqual([]);
  });

  it("notifies subscribers on every mutation", () => {
    let calls = 0;
    const unsubscribe = tokenStore.subscribe(() => {
      calls += 1;
    });

    tokenStore.setAccessToken("a");
    tokenStore.setTenant("acme");
    unsubscribe();
    tokenStore.setAccessToken("b");

    expect(calls).toBe(2);
  });
});
