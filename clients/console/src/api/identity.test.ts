import { beforeEach, describe, expect, it, vi } from "vitest";
import { getMyProfile, isProfileConflict, updateMyProfile, type MyProfile } from "@/api/identity";
import { ApiRequestError } from "@/lib/api-client";

// The real client builds a Request from a relative URL, which undici (jsdom's fetch) refuses; the
// question here is what the profile calls read and send, so the transport is stubbed out.
const { get, put } = vi.hoisted(() => ({ get: vi.fn(), put: vi.fn() }));
vi.mock("@/lib/api-client", () => {
  class ApiRequestError extends Error {
    readonly status: number;
    constructor(status: number, message: string) {
      super(message);
      this.status = status;
    }
  }
  return {
    api: { GET: get, PUT: put },
    ApiRequestError,
    AS_OPERATOR: {},
    unwrap: (r: { data: unknown }) => r.data,
    unwrapVoid: () => undefined,
  };
});

function shown(overrides: Partial<MyProfile> = {}): MyProfile {
  return {
    id: "u-1",
    email: "ada@example.com",
    firstName: "Ada",
    lastName: "Lovelace",
    phoneNumber: "123",
    etag: '"v1"',
    ...overrides,
  } as MyProfile;
}

beforeEach(() => {
  get.mockReset();
  put.mockReset();
  put.mockResolvedValue({ data: undefined, response: new Response(null, { status: 200 }) });
});

describe("getMyProfile", () => {
  it("returns the profile's version from the ETag header", async () => {
    get.mockResolvedValue({
      data: { id: "u-1", firstName: "Ada" },
      response: new Response(null, { status: 200, headers: { ETag: '"v1"' } }),
    });

    const profile = await getMyProfile();

    expect(profile.firstName).toBe("Ada");
    expect(profile.etag).toBe('"v1"');
  });
});

describe("updateMyProfile", () => {
  it("sends the version the user was shown in If-Match", async () => {
    await updateMyProfile(shown(), { firstName: "Grace" });

    const [, init] = put.mock.calls[0];
    expect(init.params.header["If-Match"]).toBe('"v1"');
    expect(init.body).toMatchObject({ firstName: "Grace", lastName: "Lovelace", phoneNumber: "123" });
  });

  it("fills unset fields from the shown profile instead of re-reading it", async () => {
    // A fresh read would carry the newest version, and the If-Match would then protect nothing.
    await updateMyProfile(shown(), { deleteCurrentImage: true });

    expect(get).not.toHaveBeenCalled();
    expect(put.mock.calls[0][1].body).toMatchObject({ firstName: "Ada", deleteCurrentImage: true });
  });

  it("refuses to save without a version rather than sending an unconditional PUT", async () => {
    await expect(updateMyProfile(shown({ etag: null }), { firstName: "Grace" })).rejects.toThrow();
    expect(put).not.toHaveBeenCalled();
  });
});

describe("isProfileConflict", () => {
  it("is true only for a 412", () => {
    expect(isProfileConflict(new ApiRequestError(412, "Precondition Failed"))).toBe(true);
    expect(isProfileConflict(new ApiRequestError(400, "Bad Request"))).toBe(false);
    expect(isProfileConflict(new Error("boom"))).toBe(false);
  });
});
