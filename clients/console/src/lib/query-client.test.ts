import { afterEach, describe, expect, it } from "vitest";
import { endSessionLocally, queryClient } from "@/lib/query-client";
import { actingStore, type ActingSession } from "@/auth/acting-store";

const ACTING: ActingSession = {
  accessToken: "acting-token",
  tenantId: "acme",
  userId: "u-9",
  expiresAt: new Date(Date.now() + 900_000).toISOString(),
  jti: "jti-1",
};

describe("endSessionLocally", () => {
  afterEach(() => {
    actingStore.clear();
    localStorage.clear();
    queryClient.clear();
  });

  it("drops the acting session, clears stored tokens, and empties the query cache", () => {
    actingStore.start(ACTING);
    localStorage.setItem("boilerplate.console.accessToken", "operator-token");
    localStorage.setItem("boilerplate.console.tenant", "acme");
    localStorage.setItem("boilerplate.console.permissions", JSON.stringify(["Users.View"]));
    queryClient.setQueryData(["users"], [{ id: "1" }]);

    endSessionLocally();

    expect(actingStore.get()).toBeNull();
    expect(localStorage.getItem("boilerplate.console.accessToken")).toBeNull();
    expect(localStorage.getItem("boilerplate.console.permissions")).toBeNull();
    expect(queryClient.getQueryData(["users"])).toBeUndefined();
  });

  it("is a no-op when there is nothing to clear", () => {
    expect(() => endSessionLocally()).not.toThrow();
    expect(actingStore.get()).toBeNull();
  });
});
