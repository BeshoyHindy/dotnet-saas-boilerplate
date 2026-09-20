import { afterEach, describe, expect, it } from "vitest";
import { endSessionLocally, queryClient } from "@/lib/query-client";

describe("endSessionLocally", () => {
  afterEach(() => {
    localStorage.clear();
    queryClient.clear();
  });

  it("clears stored tokens and empties the query cache", () => {
    localStorage.setItem("boilerplate.dashboard.accessToken", "user-token");
    localStorage.setItem("boilerplate.dashboard.tenant", "acme");
    localStorage.setItem("boilerplate.dashboard.permissions", JSON.stringify(["Users.View"]));
    queryClient.setQueryData(["users"], [{ id: "1" }]);

    endSessionLocally();

    expect(localStorage.getItem("boilerplate.dashboard.accessToken")).toBeNull();
    expect(localStorage.getItem("boilerplate.dashboard.permissions")).toBeNull();
    expect(queryClient.getQueryData(["users"])).toBeUndefined();
  });

  it("is a no-op when there is nothing to clear", () => {
    expect(() => endSessionLocally()).not.toThrow();
    expect(localStorage.getItem("boilerplate.dashboard.accessToken")).toBeNull();
  });
});
