// Branch guard — keeps ungated work off the branches that reach the merge.
//
// The reviewer is told to commit BEFORE it starts the long gates, so a reviewer
// that dies mid-gates can leave commits no gate ever passed. Absorbing that
// failure (so the implementer's gated work still merges) is only safe if the
// branch can be shown to hold nothing beyond what the implementer validated.
//
// Everything here FAILS CLOSED. The one rule: a branch merges only when git
// PROVES it points at the validated tip. "git did not answer" is not "the
// branch is fine" — indeterminate is its own state, and it refuses. A guard
// that shrugs and merges anyway is not a guard.
//
// Lives outside main.mts so it can be tested against a real repository —
// main.mts binds the singleton sentinel and starts a run on import.

import { spawnSync } from "node:child_process";

export type GitResult = { status: number; stdout: string; stderr: string };
export type GitRunner = (args: readonly string[]) => GitResult;

// spawnSync rather than execFileSync: a non-zero exit is data here, not an
// exception. Collapsing "ref does not exist" (exit 1) and "the repository is
// broken" (exit 128, or git missing entirely) into one thrown error is what
// made the previous guard treat an unanswerable question as a safe answer.
export const gitIn =
  (cwd: string): GitRunner =>
  (args) => {
    const run = spawnSync("git", [...args], { cwd, encoding: "utf8" });
    if (run.error !== undefined) {
      return { status: -1, stdout: "", stderr: String(run.error) };
    }
    return {
      status: run.status ?? -1,
      stdout: (run.stdout ?? "").trim(),
      stderr: (run.stderr ?? "").trim(),
    };
  };

export type BranchTip =
  /** git resolved the branch to this commit. */
  | { state: "at"; tip: string }
  /** git is certain the branch does not exist. */
  | { state: "absent" }
  /** git could not answer. Never treat this as either of the above. */
  | { state: "unknown"; reason: string };

const SHA = /^[0-9a-f]{40}$/;

/**
 * Resolve a branch to its commit.
 *
 * `rev-parse --verify --quiet` exits 1 with empty output for a ref that is
 * simply not there, and 128 (or worse) when git itself failed — so the two are
 * reported as different states rather than one null.
 */
export function lookupBranchTip(git: GitRunner, branch: string): BranchTip {
  const result = git([
    "rev-parse",
    "--verify",
    "--quiet",
    `refs/heads/${branch}^{commit}`,
  ]);

  if (result.status === 0 && SHA.test(result.stdout)) {
    return { state: "at", tip: result.stdout };
  }
  if (result.status === 1 && result.stdout === "") return { state: "absent" };

  return {
    state: "unknown",
    reason:
      `git rev-parse exited ${result.status} for ${branch}` +
      (result.stderr === "" ? "" : `: ${result.stderr}`),
  };
}

/**
 * The branch's commit, or null when it is absent OR unknown.
 *
 * Only for callers that treat both as "cannot prove anything about this
 * branch" — which is what recording a validated tip requires. Anything making
 * a merge decision must use lookupBranchTip and handle the states apart.
 */
export function branchTip(git: GitRunner, branch: string): string | null {
  const found = lookupBranchTip(git, branch);
  return found.state === "at" ? found.tip : null;
}

export type RestoreOutcome =
  | { restored: true; movedFrom?: string; rescueRef?: string }
  | { restored: false; reason: string };

/**
 * Ensure `branch` points at `validatedTip`, parking anything beyond it on a
 * rescue ref first so nothing is destroyed.
 *
 * Returns restored:true ONLY when git confirms the branch is at `validatedTip`.
 * Every other case — including a branch that has vanished, or a git that will
 * not answer — returns false, and the caller must keep the branch out of the
 * merge. Refusing to merge a vanished branch costs nothing (it has no commits
 * to contribute) and keeps the contract a single provable statement.
 *
 * MUST run after the sandbox is closed: while it is open the branch is checked
 * out in Sandcastle's host-side worktree and git refuses to move it
 * ("cannot force update the branch used by worktree at ...").
 */
export function restoreValidatedBranch(
  git: GitRunner,
  issueId: string,
  branch: string,
  validatedTip: string,
): RestoreOutcome {
  const current = lookupBranchTip(git, branch);

  if (current.state === "unknown") {
    return {
      restored: false,
      reason: `could not read ${branch} (${current.reason})`,
    };
  }
  if (current.state === "absent") {
    return { restored: false, reason: `${branch} no longer exists` };
  }
  if (current.tip === validatedTip) return { restored: true };

  const rescueRef = `sandcastle/review-wip/${issueId}-${current.tip.slice(0, 7)}`;
  const parked = git(["branch", "-f", rescueRef, current.tip]);
  if (parked.status !== 0) {
    return {
      restored: false,
      reason: `could not park ${branch} on ${rescueRef}: ${parked.stderr}`,
    };
  }

  const rewound = git(["branch", "-f", branch, validatedTip]);
  if (rewound.status !== 0) {
    return {
      restored: false,
      reason:
        `git refused to rewind ${branch} to ${validatedTip.slice(0, 7)}: ` +
        rewound.stderr,
    };
  }

  // Trust the write only if git agrees the branch actually moved.
  const afterwards = lookupBranchTip(git, branch);
  if (afterwards.state !== "at" || afterwards.tip !== validatedTip) {
    return {
      restored: false,
      reason:
        `${branch} is ${JSON.stringify(afterwards)} after the rewind, not the ` +
        `validated tip ${validatedTip.slice(0, 7)}`,
    };
  }

  return { restored: true, movedFrom: current.tip, rescueRef };
}
