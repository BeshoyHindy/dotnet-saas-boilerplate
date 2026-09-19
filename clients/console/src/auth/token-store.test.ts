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

  it("stashes the operator's session when entering a tenant and restores it on end", () => {
    tokenStore.setAccessToken("operator-token");
    tokenStore.setTenant("root");
    tokenStore.setPermissions(["Permissions.Tenants.View"]);

    tokenStore.beginImpersonation("exchanged-token", "acme");

    expect(tokenStore.getAccessToken()).toBe("exchanged-token");
    expect(tokenStore.getTenant()).toBe("acme");
    // The impersonated subject has its own grants; the operator's must not leak.
    expect(tokenStore.getPermissions()).toEqual([]);
    expect(tokenStore.hasImpersonationStash()).toBe(true);

    tokenStore.endImpersonationWithFreshAccessToken("fresh-operator-token");

    expect(tokenStore.getAccessToken()).toBe("fresh-operator-token");
    expect(tokenStore.getTenant()).toBe("root");
    expect(tokenStore.hasImpersonationStash()).toBe(false);
  });

  it("restores the stashed operator session when ending impersonation fails", () => {
    tokenStore.setAccessToken("operator-token");
    tokenStore.setTenant("root");
    tokenStore.beginImpersonation("exchanged-token", "acme");

    expect(tokenStore.restoreStashedActor()).toBe(true);
    expect(tokenStore.getAccessToken()).toBe("operator-token");
    expect(tokenStore.getTenant()).toBe("root");
    expect(tokenStore.hasImpersonationStash()).toBe(false);
  });

  it("clear() drops the session and any half-finished impersonation", () => {
    tokenStore.setAccessToken("operator-token");
    tokenStore.setTenant("root");
    tokenStore.beginImpersonation("exchanged-token", "acme");

    tokenStore.clear();

    expect(tokenStore.getAccessToken()).toBeNull();
    expect(tokenStore.hasImpersonationStash()).toBe(false);
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
