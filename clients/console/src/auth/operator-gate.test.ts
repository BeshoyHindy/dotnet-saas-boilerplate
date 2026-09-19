import { describe, expect, it } from "vitest";
import { isOperator } from "@/auth/operator-gate";
import { IdentityPermissions, MultitenancyPermissions, SystemPermissions } from "@/lib/permissions";

describe("isOperator", () => {
  it("accepts the tenant registry grant", () => {
    expect(isOperator([MultitenancyPermissions.Tenants.View])).toBe(true);
  });

  it("accepts the cross-tenant exchange grant on its own", () => {
    // A platform operator whose role carries the exchange but not the registry still
    // belongs here — "enter tenant" is the console's whole point.
    expect(isOperator([SystemPermissions.Platform.CrossTenantImpersonate])).toBe(true);
  });

  it("refuses a tenant administrator, however senior", () => {
    // Full control of one tenant is not platform operation: Users.Impersonate is the
    // SAME-tenant grant (issue #9), and the server refuses a cross-tenant start with it.
    expect(
      isOperator([
        IdentityPermissions.Users.Update,
        IdentityPermissions.Users.Impersonate,
        IdentityPermissions.Roles.Update,
        IdentityPermissions.Sessions.ViewAll,
      ]),
    ).toBe(false);
  });

  it("refuses an empty grant", () => {
    expect(isOperator([])).toBe(false);
  });
});
