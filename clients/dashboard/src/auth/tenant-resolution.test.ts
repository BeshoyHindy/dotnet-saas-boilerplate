import { afterEach, describe, expect, it } from "vitest";
import {
  forgetRememberedTenant,
  readRememberedTenant,
  rememberTenant,
  resolveTenant,
  tenantFromHost,
  tenantFromQuery,
} from "@/auth/tenant-resolution";

describe("tenantFromHost", () => {
  it("reads the tenant off a dev subdomain", () => {
    expect(tenantFromHost("acme.localhost:5173")).toBe("acme");
  });

  it("reads the tenant off a deployed subdomain", () => {
    expect(tenantFromHost("acme.app.example.com")).toBe("acme");
  });

  it.each(["localhost", "localhost:5173", "example.com", "example.com:8081"])(
    "refuses the bare host %s — there is no tenant label",
    (host) => {
      expect(tenantFromHost(host)).toBeNull();
    },
  );

  it.each(["127.0.0.1", "127.0.0.1:5173", "10.0.0.5"])("refuses the IP literal %s", (host) => {
    // "127" is not a tenant, and an IP has no DNS labels to read.
    expect(tenantFromHost(host)).toBeNull();
  });

  it("refuses an IPv6 literal", () => {
    expect(tenantFromHost("[::1]:5173")).toBeNull();
  });

  it.each(["www.example.com", "app.example.com", "console.example.com", "api.example.com"])(
    "refuses the reserved label in %s",
    (host) => {
      expect(tenantFromHost(host)).toBeNull();
    },
  );

  it("is case- and trailing-dot-insensitive", () => {
    expect(tenantFromHost("ACME.localhost.")).toBe("acme");
  });

  it("refuses a first label that is not a valid identifier", () => {
    expect(tenantFromHost("-bad.example.com")).toBeNull();
    expect(tenantFromHost("a_b.example.com")).toBeNull();
  });

  it("handles a missing host", () => {
    expect(tenantFromHost(undefined)).toBeNull();
    expect(tenantFromHost("")).toBeNull();
  });
});

describe("tenantFromQuery", () => {
  it("reads ?tenant= as the mailed links write it", () => {
    expect(tenantFromQuery("?tenant=acme&code=xyz")).toBe("acme");
  });

  it("ignores an absent or empty parameter", () => {
    expect(tenantFromQuery("?code=xyz")).toBeNull();
    expect(tenantFromQuery("?tenant=")).toBeNull();
    expect(tenantFromQuery("")).toBeNull();
  });

  it("refuses a value that is not a tenant identifier", () => {
    // Nothing downstream should ever build a URL out of this.
    expect(tenantFromQuery("?tenant=../../etc")).toBeNull();
    expect(tenantFromQuery("?tenant=a%20b")).toBeNull();
  });
});

describe("the remembered tenant", () => {
  afterEach(() => {
    forgetRememberedTenant();
  });

  it("round-trips the tenant id", () => {
    rememberTenant("acme");
    expect(readRememberedTenant()).toBe("acme");
  });

  it("stores the tenant id and nothing else", () => {
    rememberTenant("acme");
    // One key, and it holds an identifier — never a token (ADR-0002).
    expect(Object.keys(localStorage)).toEqual(["boilerplate.dashboard.lastTenant"]);
    expect(localStorage.getItem("boilerplate.dashboard.lastTenant")).toBe("acme");
  });

  it("ignores a junk value already in storage", () => {
    localStorage.setItem("boilerplate.dashboard.lastTenant", "not a tenant");
    expect(readRememberedTenant()).toBeNull();
  });
});

describe("resolveTenant", () => {
  const defaultTenant = "root";

  it("prefers the query parameter over everything", () => {
    expect(
      resolveTenant({
        search: "?tenant=frommail",
        host: "acme.app.example.com",
        remembered: "globex",
        defaultTenant,
      }),
    ).toEqual({ tenant: "frommail", source: "query", certain: true });
  });

  it("falls to the subdomain next", () => {
    expect(
      resolveTenant({ search: "", host: "acme.localhost:5173", remembered: "globex", defaultTenant }),
    ).toEqual({ tenant: "acme", source: "subdomain", certain: true });
  });

  it("falls to the remembered tenant when the arrival says nothing", () => {
    expect(
      resolveTenant({ search: "", host: "localhost:5173", remembered: "globex", defaultTenant }),
    ).toEqual({ tenant: "globex", source: "remembered", certain: false });
  });

  it("falls to the configured default last", () => {
    expect(resolveTenant({ search: "", host: "localhost:5173", defaultTenant })).toEqual({
      tenant: "root",
      source: "default",
      certain: false,
    });
  });

  it("marks only an arrival-named tenant certain — a guess keeps the field visible", () => {
    expect(resolveTenant({ host: "acme.localhost", defaultTenant }).certain).toBe(true);
    expect(resolveTenant({ host: "localhost", remembered: "acme", defaultTenant }).certain).toBe(
      false,
    );
  });
});
