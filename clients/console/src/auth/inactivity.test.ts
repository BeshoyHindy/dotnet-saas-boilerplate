import { afterEach, describe, expect, it } from "vitest";
import { consumeSignedOutReason, markSignedOut, signedOutNotice } from "@/auth/inactivity";

describe("signed-out reason", () => {
  afterEach(() => sessionStorage.clear());

  it("is read back once, then gone", () => {
    markSignedOut("expired");

    expect(consumeSignedOutReason()).toBe("expired");
    expect(consumeSignedOutReason()).toBeNull();
  });

  it("gives the login page a notice for each involuntary ending", () => {
    expect(signedOutNotice("inactivity")).toBe("You were signed out due to inactivity.");
    expect(signedOutNotice("expired")).toBe("Your session has ended. Please sign in again.");
  });

  it("says nothing after a deliberate sign-out or for a reason it does not know", () => {
    expect(signedOutNotice(null)).toBeNull();
    expect(signedOutNotice("toString")).toBeNull();
  });
});
