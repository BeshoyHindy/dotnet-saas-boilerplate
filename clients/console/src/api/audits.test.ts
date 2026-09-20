import { describe, expect, it } from "vitest";
import { AuditTag, auditPredicate, decodeTags, severityRank } from "@/api/audits";

describe("decodeTags", () => {
  it("decodes a bitmask into the tags it carries", () => {
    expect(decodeTags(AuditTag.PiiMasked | AuditTag.Authentication)).toEqual([
      "PII masked",
      "Auth",
    ]);
  });

  it("decodes an empty mask to nothing", () => {
    expect(decodeTags(AuditTag.None)).toEqual([]);
  });
});

describe("severityRank", () => {
  it("orders severities so thresholds can compare them", () => {
    expect(severityRank("Critical")).toBeGreaterThan(severityRank("Warning"));
    expect(severityRank("Warning")).toBeGreaterThan(severityRank("Information"));
  });
});

describe("auditPredicate", () => {
  it("reads an activity row as a sentence", () => {
    expect(auditPredicate({ eventType: "Activity", source: "api.identity.ListUsers" })).toBe(
      "viewed users",
    );
  });

  it("names the login row by its override", () => {
    expect(auditPredicate({ eventType: "Security", source: "api.identity.IssueJwtToken" })).toBe(
      "signed in",
    );
  });

  it("summarises an entity change at record level", () => {
    expect(auditPredicate({ eventType: "EntityChange", source: "IdentityDbContext" })).toBe(
      "changed identity records",
    );
  });

  it("locates an exception", () => {
    expect(auditPredicate({ eventType: "Exception", source: "api.files.RequestUploadUrl" })).toBe(
      "hit an error in request upload url",
    );
  });

  it("degrades gracefully when the source is missing", () => {
    expect(auditPredicate({ eventType: "Activity", source: null })).toBe("performed an action");
  });
});
