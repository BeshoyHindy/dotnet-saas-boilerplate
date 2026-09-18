import assert from "node:assert/strict";
import { mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";

import {
  branchTip,
  gitIn,
  lookupBranchTip,
  restoreValidatedBranch,
  type GitRunner,
} from "./branch-guard.mts";

// These run against a real repository on purpose: the guard's whole job is to
// be right about what git actually does, and git's refusals — a checked-out
// branch especially — are not worth mocking.
function withRepo(body: (repo: string, git: GitRunner) => void) {
  const repo = mkdtempSync(join(tmpdir(), "branch-guard-"));
  try {
    const git = gitIn(repo);
    const ok = (...args: string[]) => {
      const result = git(args);
      assert.equal(result.status, 0, `git ${args.join(" ")}: ${result.stderr}`);
      return result.stdout;
    };
    ok("init", "-q", "-b", "main", ".");
    ok("config", "user.email", "test@example.com");
    ok("config", "user.name", "Test");
    writeFileSync(join(repo, "f"), "base\n");
    ok("add", "-A");
    ok("commit", "-qm", "base");
    body(repo, git);
  } finally {
    rmSync(repo, { recursive: true, force: true });
  }
}

/** Commit `content` on `branch` and return the new tip. */
function commitOn(
  repo: string,
  git: GitRunner,
  branch: string,
  content: string,
): string {
  git(["checkout", "-q", branch]);
  writeFileSync(join(repo, "f"), `${content}\n`);
  git(["commit", "-qam", content]);
  const tip = git(["rev-parse", "HEAD"]).stdout;
  git(["checkout", "-q", "main"]);
  return tip;
}

/** A runner whose git is unreachable — every question is unanswerable. */
const brokenGit: GitRunner = () => ({
  status: -1,
  stdout: "",
  stderr: "spawn git ENOENT",
});

// --- lookupBranchTip -------------------------------------------------------

test("resolves an existing branch to its commit", () => {
  withRepo((repo, git) => {
    git(["branch", "issue-1"]);
    const tip = commitOn(repo, git, "issue-1", "implementer");

    assert.deepEqual(lookupBranchTip(git, "issue-1"), { state: "at", tip });
  });
});

test("reports a missing branch as absent, not unknown", () => {
  withRepo((_repo, git) => {
    assert.deepEqual(lookupBranchTip(git, "issue-nope"), { state: "absent" });
  });
});

// The distinction the previous guard collapsed: git failing to answer is not
// evidence the branch is missing.
test("reports a git failure as unknown, not absent", () => {
  const found = lookupBranchTip(brokenGit, "issue-1");

  assert.equal(found.state, "unknown");
  assert.match(found.state === "unknown" ? found.reason : "", /ENOENT/);
});

test("branchTip flattens absent and unknown alike to null", () => {
  withRepo((_repo, git) => {
    assert.equal(branchTip(git, "issue-nope"), null);
    assert.equal(branchTip(brokenGit, "issue-1"), null);
  });
});

// --- restoreValidatedBranch ------------------------------------------------

test("is a no-op when the branch is already at the validated tip", () => {
  withRepo((repo, git) => {
    git(["branch", "issue-1"]);
    const validated = commitOn(repo, git, "issue-1", "implementer");

    assert.deepEqual(restoreValidatedBranch(git, "1", "issue-1", validated), {
      restored: true,
    });
    assert.equal(branchTip(git, "issue-1"), validated);
  });
});

test("rewinds ungated reviewer commits and keeps them on a rescue ref", () => {
  withRepo((repo, git) => {
    git(["branch", "issue-1"]);
    const validated = commitOn(repo, git, "issue-1", "implementer");
    const ungated = commitOn(repo, git, "issue-1", "reviewer-ungated");

    const outcome = restoreValidatedBranch(git, "1", "issue-1", validated);

    assert.equal(outcome.restored, true);
    assert.equal(branchTip(git, "issue-1"), validated);

    // Nothing is destroyed: the discarded commit is still reachable.
    const rescueRef = outcome.restored ? outcome.rescueRef : undefined;
    assert.ok(rescueRef);
    assert.equal(branchTip(git, rescueRef), ungated);
    assert.match(
      git(["log", "--oneline", "issue-1"]).stdout,
      /implementer/,
    );
    assert.doesNotMatch(
      git(["log", "--oneline", "issue-1"]).stdout,
      /reviewer-ungated/,
    );
  });
});

// The failure that must NOT fail open: git refuses to move a branch checked
// out in a worktree, which is exactly the state while the sandbox is open.
test("fails closed when git refuses to move a checked-out branch", () => {
  withRepo((repo, git) => {
    git(["branch", "issue-1"]);
    const validated = commitOn(repo, git, "issue-1", "implementer");
    commitOn(repo, git, "issue-1", "reviewer-ungated");
    git(["worktree", "add", "-q", join(repo, "wt"), "issue-1"]);

    const outcome = restoreValidatedBranch(git, "1", "issue-1", validated);

    assert.equal(outcome.restored, false);
    assert.match(
      outcome.restored === false ? outcome.reason : "",
      /refused to rewind/,
    );
    // The branch really is still carrying the ungated commit, which is why the
    // caller must keep it out of the merge.
    assert.match(
      git(["log", "--oneline", "issue-1"]).stdout,
      /reviewer-ungated/,
    );
  });
});

test("fails closed when the validated tip is not a real commit", () => {
  withRepo((repo, git) => {
    git(["branch", "issue-1"]);
    commitOn(repo, git, "issue-1", "implementer");
    commitOn(repo, git, "issue-1", "reviewer-ungated");

    const outcome = restoreValidatedBranch(
      git,
      "1",
      "issue-1",
      "0000000000000000000000000000000000000000",
    );

    assert.equal(outcome.restored, false);
  });
});

// Fail-open #1 in the previous guard: a git that cannot answer was read as
// "the branch is gone", which was read as "safe to merge".
test("fails closed when git cannot answer at all", () => {
  const outcome = restoreValidatedBranch(brokenGit, "1", "issue-1", "abc1234");

  assert.equal(outcome.restored, false);
  assert.match(outcome.restored === false ? outcome.reason : "", /could not read/);
});

// Fail-open #2: a vanished branch was reported as restored. It has nothing to
// contribute to a merge, so refusing costs nothing and keeps the contract to a
// single provable statement.
test("fails closed when the branch has vanished", () => {
  withRepo((repo, git) => {
    git(["branch", "issue-1"]);
    const validated = commitOn(repo, git, "issue-1", "implementer");
    git(["branch", "-D", "issue-1"]);

    const outcome = restoreValidatedBranch(git, "1", "issue-1", validated);

    assert.equal(outcome.restored, false);
    assert.match(
      outcome.restored === false ? outcome.reason : "",
      /no longer exists/,
    );
  });
});
