import { describe, expect, it } from "vitest";
import {
  ApiRequestError,
  authPath,
  isTenantDeactivatedError,
  unwrap,
  unwrapVoid,
} from "@/lib/api-client";

function result<T>(init: { data?: T; error?: unknown; status: number }) {
  return {
    data: init.data,
    error: init.error,
    response: new Response(null, { status: init.status }),
  };
}

describe("authPath", () => {
  it("puts the tenant in the path — the one place a caller may name one", () => {
    expect(authPath("acme", "token")).toBe("/api/v1/tenants/acme/auth/token");
  });

  it("encodes a tenant id that is not URL-safe", () => {
    expect(authPath("a c/me", "refresh")).toBe("/api/v1/tenants/a%20c%2Fme/auth/refresh");
  });
});

describe("unwrap", () => {
  it("returns the body of a successful call", () => {
    expect(unwrap(result({ data: { id: "u1" }, status: 200 }))).toEqual({ id: "u1" });
  });

  it("returns undefined for a 204", () => {
    expect(unwrapVoid(result({ status: 204 }))).toBeUndefined();
  });

  it("throws an ApiRequestError carrying the problem details", () => {
    const problem = { status: 400, title: "Validation failed", detail: "Email is required" };
    try {
      unwrap(result({ error: problem, status: 400 }));
      expect.unreachable("unwrap must throw on a non-OK response");
    } catch (error) {
      expect(error).toBeInstanceOf(ApiRequestError);
      expect((error as ApiRequestError).status).toBe(400);
      expect((error as ApiRequestError).message).toBe("Validation failed");
      expect((error as ApiRequestError).problem).toEqual(problem);
    }
  });

  it("falls back to the status text when the body is not problem+json", () => {
    expect(() => unwrap(result({ status: 500 }))).toThrow(ApiRequestError);
  });
});

describe("isTenantDeactivatedError", () => {
  it("recognises the deactivated-tenant 403 by its detail text", () => {
    const error = new ApiRequestError(403, "Forbidden", {
      status: 403,
      detail: "This tenant has been deactivated.",
    });
    expect(isTenantDeactivatedError(error)).toBe(true);
  });

  it("ignores any other 403", () => {
    const error = new ApiRequestError(403, "Forbidden", { status: 403, detail: "Missing permission" });
    expect(isTenantDeactivatedError(error)).toBe(false);
  });

  it("ignores non-API errors", () => {
    expect(isTenantDeactivatedError(new Error("boom"))).toBe(false);
  });
});
