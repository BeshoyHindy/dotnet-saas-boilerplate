import assert from "node:assert/strict";
import test from "node:test";

import {
  runIssuePipeline,
  selectMergeableIssues,
  partitionLandedIssues,
  type PipelinePhaseResult,
} from "./issue-pipeline.mts";

const phase = (...commits: string[]): PipelinePhaseResult => ({ commits });

const fulfilled = (
  ...commits: string[]
): PromiseSettledResult<PipelinePhaseResult> => ({
  status: "fulfilled",
  value: phase(...commits),
});

const rejected = (reason: string): PromiseSettledResult<PipelinePhaseResult> => ({
  status: "rejected",
  reason: new Error(reason),
});

// --- runIssuePipeline ------------------------------------------------------

test("returns both phases' commits when implementer and reviewer succeed", async () => {
  const outcome = await runIssuePipeline({
    runImplementer: async () => phase("impl-1", "impl-2"),
    runReviewer: async () => phase("review-1"),
  });

  assert.deepEqual(outcome.commits, ["impl-1", "impl-2", "review-1"]);
  assert.equal(outcome.reviewFailure, undefined);
});

test("skips the reviewer when the implementer produced no commits", async () => {
  let reviewerRan = false;

  const outcome = await runIssuePipeline({
    runImplementer: async () => phase(),
    runReviewer: async () => {
      reviewerRan = true;
      return phase("review-1");
    },
  });

  assert.equal(reviewerRan, false);
  assert.deepEqual(outcome.commits, []);
});

// The founding incident: the implementer committed twice and Sandcastle synced
// both commits to the host branch, then the reviewer died on a too-large
// prompt. The reviewer's throw must not discard that work.
test("keeps the implementer's commits when the reviewer throws", async () => {
  const outcome = await runIssuePipeline({
    runImplementer: async () => phase("impl-1", "impl-2"),
    runReviewer: async () => {
      throw new Error("Prompt is too long");
    },
  });

  assert.deepEqual(outcome.commits, ["impl-1", "impl-2"]);
  assert.match(String(outcome.reviewFailure), /Prompt is too long/);
});

// The reviewer commits BEFORE it starts the long gates, so a reviewer that
// dies mid-gates can leave commits no gate ever passed. The caller needs the
// validated point marked before the reviewer can touch the branch.
test("marks the validated point after the implementer, before the reviewer", async () => {
  const order: string[] = [];

  await runIssuePipeline({
    runImplementer: async () => {
      order.push("implement");
      return phase("impl-1");
    },
    runReviewer: async () => {
      order.push("review");
      return phase("review-1");
    },
    captureValidated: () => {
      order.push("validated");
      return "tip-sha";
    },
  });

  assert.deepEqual(order, ["implement", "validated", "review"]);
});

test("reports the validated tip when the reviewer then dies", async () => {
  const outcome = await runIssuePipeline({
    runImplementer: async () => phase("impl-1"),
    runReviewer: async () => {
      throw new Error("killed mid-gates");
    },
    captureValidated: () => "tip-sha",
  });

  assert.equal(outcome.validatedTip, "tip-sha");
});

// Absent marker means "cannot prove this branch is gated" — the caller must
// refuse to merge it rather than assume the branch is clean.
test("reports no validated tip when it could not be captured", async () => {
  const outcome = await runIssuePipeline({
    runImplementer: async () => phase("impl-1"),
    runReviewer: async () => {
      throw new Error("killed mid-gates");
    },
    captureValidated: () => null,
  });

  assert.notEqual(outcome.reviewFailure, undefined);
  assert.equal(outcome.validatedTip, undefined);
});

test("captures no validated point when the implementer produced nothing", async () => {
  let captured = false;

  await runIssuePipeline({
    runImplementer: async () => phase(),
    runReviewer: async () => phase("review-1"),
    captureValidated: () => {
      captured = true;
      return "tip-sha";
    },
  });

  assert.equal(captured, false);
});

test("reports the reviewer failure to the caller", async () => {
  const seen: unknown[] = [];

  await runIssuePipeline({
    runImplementer: async () => phase("impl-1"),
    runReviewer: async () => {
      throw new Error("sandbox crashed");
    },
    onReviewFailure: (error) => seen.push(error),
  });

  assert.equal(seen.length, 1);
  assert.match(String(seen[0]), /sandbox crashed/);
});

test("lets onReviewFailure abort the pipeline by re-throwing", async () => {
  await assert.rejects(
    runIssuePipeline({
      runImplementer: async () => phase("impl-1"),
      runReviewer: async () => {
        throw new Error("The provided authorization grant is invalid");
      },
      onReviewFailure: (error) => {
        throw new Error(`fatal: ${String(error)}`);
      },
    }),
    /fatal:.*authorization grant is invalid/,
  );
});

test("propagates an implementer failure", async () => {
  await assert.rejects(
    runIssuePipeline({
      runImplementer: async () => {
        throw new Error("implementer died");
      },
      runReviewer: async () => phase("review-1"),
    }),
    /implementer died/,
  );
});

// --- selectMergeableIssues -------------------------------------------------

const issue = (id: string) => ({ id, branch: `agent/issue-${id}` });

test("selects issues whose pipeline produced commits", () => {
  const issues = [issue("1"), issue("2")];

  const selection = selectMergeableIssues(
    issues,
    [fulfilled("a"), fulfilled()],
    () => 0,
  );

  assert.deepEqual(selection.mergeable, [issues[0]]);
  assert.deepEqual(selection.rescued, []);
  assert.deepEqual(selection.stranded, []);
});

// A crashed implementer can leave half-finished, never-gated commits on the
// branch. Merging those on the strength of git alone would land a broken tree
// on the host branch — the red HEAD this guard exists to prevent.
test("never merges a failed pipeline's commits, and reports them instead", () => {
  const issues = [issue("1")];

  const selection = selectMergeableIssues(
    issues,
    [rejected("implementer OOM-killed mid-feature")],
    () => 3,
  );

  assert.deepEqual(selection.mergeable, []);
  assert.deepEqual(selection.rescued, []);
  assert.deepEqual(selection.stranded, issues);
});

test("does not report a failed pipeline that left no commits behind", () => {
  const selection = selectMergeableIssues(
    [issue("1")],
    [rejected("sandbox never started")],
    () => 0,
  );

  assert.deepEqual(selection.mergeable, []);
  assert.deepEqual(selection.rescued, []);
  assert.deepEqual(selection.stranded, []);
});

// That incident's shape on its next round: the pipeline completes cleanly (the
// implementer finds nothing left to do and runs the gates) while the branch
// still carries the commits an earlier broken round stranded.
test("rescues a completed pipeline whose branch is still ahead of the target", () => {
  const issues = [issue("7")];

  const selection = selectMergeableIssues(issues, [fulfilled()], () => 2);

  assert.deepEqual(selection.mergeable, issues);
  assert.deepEqual(selection.rescued, issues);
  assert.deepEqual(selection.stranded, []);
});

test("keeps issues in plan order and asks git only about the branch in hand", () => {
  const issues = [issue("1"), issue("2"), issue("3"), issue("4")];
  const asked: string[] = [];

  const selection = selectMergeableIssues(
    issues,
    [rejected("died"), fulfilled("a"), rejected("died"), fulfilled()],
    (branch) => {
      asked.push(branch);
      return branch === "agent/issue-1" ? 0 : 4;
    },
  );

  assert.deepEqual(
    selection.mergeable.map((i) => i.id),
    ["2", "4"],
  );
  assert.deepEqual(
    selection.stranded.map((i) => i.id),
    ["3"],
  );
  // Only branches without a reported commit are looked up.
  assert.deepEqual(asked, [
    "agent/issue-1",
    "agent/issue-3",
    "agent/issue-4",
  ]);
});

test("closes only the issues whose branch actually reached HEAD", () => {
  const issues = [
    { id: "1", branch: "agent/issue-1" },
    { id: "2", branch: "agent/issue-2" },
    { id: "3", branch: "agent/issue-3" },
  ];

  // The merger landed two branches, then hit its iteration cap. That RESOLVES
  // rather than throwing, so the round previously closed all three.
  const selection = partitionLandedIssues(
    issues,
    (branch) => branch !== "agent/issue-3",
  );

  assert.deepEqual(
    selection.landed.map((i) => i.id),
    ["1", "2"],
  );
  assert.deepEqual(
    selection.unlanded.map((i) => i.id),
    ["3"],
  );
});

test("keeps every issue open when nothing landed", () => {
  const issues = [
    { id: "1", branch: "agent/issue-1" },
    { id: "2", branch: "agent/issue-2" },
  ];

  const selection = partitionLandedIssues(issues, () => false);

  assert.deepEqual(selection.landed, []);
  assert.deepEqual(
    selection.unlanded.map((i) => i.id),
    ["1", "2"],
  );
});

test("fails closed: an unanswerable ancestry check leaves the issue open", () => {
  const issues = [{ id: "1", branch: "agent/issue-1" }];

  // git unavailable, ref gone, repo dirty — the caller reports false, and the
  // issue must survive rather than be closed on a guess.
  const selection = partitionLandedIssues(issues, () => false);

  assert.deepEqual(selection.landed, []);
  assert.deepEqual(
    selection.unlanded.map((i) => i.id),
    ["1"],
  );
});

test("asks about each issue's own branch exactly once", () => {
  const asked: string[] = [];
  const issues = [
    { id: "1", branch: "agent/issue-1" },
    { id: "2", branch: "agent/issue-2" },
  ];

  partitionLandedIssues(issues, (branch) => {
    asked.push(branch);
    return true;
  });

  assert.deepEqual(asked, ["agent/issue-1", "agent/issue-2"]);
});
