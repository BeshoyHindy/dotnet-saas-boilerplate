import { beforeEach, describe, expect, it } from "vitest";
import { tokenStore } from "@/auth/token-store";

beforeEach(() => {
  localStorage.clear();
});

describe("tokenStore", () => {
  it("namespaces its keys under boilerplate.dashboard", () => {
    tokenStore.setAccessToken("access-1");
    tokenStore.setTenant("acme");

    expect(localStorage.getItem("boilerplate.dashboard.accessToken")).toBe("access-1");
    expect(localStorage.getItem("boilerplate.dashboard.tenant")).toBe("acme");
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

    localStorage.setItem("boilerplate.dashboard.permissions", "{not json");
    expect(tokenStore.getPermissions()).toEqual([]);
  });

  it("stores the session and nothing else (ADR-0002)", () => {
    tokenStore.setAccessToken("user-token");
    tokenStore.setTenant("acme");
    tokenStore.setPermissions(["Permissions.Users.View"]);

    // Three keys, and no fourth: no refresh token (it is an HttpOnly cookie) and no
    // credential naming anyone else — acting as another user is the console's job
    // (ADR-0008), and this app has no store for such a token to land in.
    expect(Object.keys(localStorage)).toEqual(
      expect.arrayContaining([
        "boilerplate.dashboard.accessToken",
        "boilerplate.dashboard.tenant",
        "boilerplate.dashboard.permissions",
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
