import { execFileSync } from "node:child_process";
import { appendFileSync, mkdirSync, statSync } from "node:fs";
import { dirname, join } from "node:path";

const SANDBOX_DIR = new URL(".", import.meta.url).pathname;
const LOGS_DIR = join(SANDBOX_DIR, "logs");

export type DirtyWorktreeReport = {
  worktreePath: string;
  statusLines: string[];
  /** Where the worktree's HEAD-relative diff was preserved; null when there was none to capture. */
  patchPath: string | null;
  /**
   * Set when the diff capture itself FAILED while the worktree still exists on disk. The
   * worktree is then the ONLY copy of the changes — the error block tells the operator to
   * inspect it directly and must never suggest removing it.
   */
  diffError: string | null;
};

/**
 * The scan verdict. `scanErrors` is the fail-closed channel: an entry means some part of the
 * scan COULD NOT run (git worktree list failed, a status check failed on a worktree that still
 * exists), so "no reports" must be read as "unverified", never as "clean". The incident
 * behind it is exactly why: a guard that swallows its own failures into a clean verdict is the
 * silent-continue it was built to prevent.
 */
export type MergerDirtScan = {
  reports: DirtyWorktreeReport[];
  scanErrors: string[];
};

/**
 * Injected git runner: returns stdout as utf8.
 *
 * Tests swap this out so they never touch the real repo's worktrees.
 */
export type GitExec = (args: string[], cwd?: string) => string;

function padTwo(n: number): string {
  return String(n).padStart(2, "0");
}

function errorMessage(err: unknown): string {
  return err instanceof Error ? err.message : String(err);
}

/**
 * Three-way presence verdict for a worktree path. "unknown" is the load-bearing
 * member: existsSync() collapses EVERY stat failure into `false`, so a worktree
 * that exists but cannot be statted (EACCES on a parent, EIO) would read as
 * "removed" and be silently skipped — a fail-open filesystem path in a guard
 * whose whole point is failing closed. Only ENOENT/ENOTDIR prove absence;
 * anything else is "could not determine" and must be treated as possibly dirty.
 */
export type PathPresence = "present" | "absent" | "unknown";

/** Injected presence probe — tests swap it to drive all three verdicts. */
export type PathPresenceProbe = (path: string) => PathPresence;

export const defaultPathPresence: PathPresenceProbe = (path) => {
  try {
    statSync(path);
    return "present";
  } catch (err) {
    const code = (err as NodeJS.ErrnoException).code;
    return code === "ENOENT" || code === "ENOTDIR" ? "absent" : "unknown";
  }
};

/** Default git runner: synchronous, throws on non-zero exit. */
export const defaultGitExec: GitExec = (args, cwd) =>
  execFileSync("git", args, { cwd, encoding: "utf8" });

/**
 * Build the patch-file path for preserved merger dirt.
 *
 * Mirrors makePostMergeGateLogPath in post-merge-gate.mts: same timestamp
 * format, same .sandcastle/logs directory. The suffix is `-merger-dirt.patch`
 * so a human scanning logs sees immediately that this is uncommitted work left
 * behind by the merger agent.
 */
export function makeMergerDirtPatchPath(timestamp = new Date()): string {
  const stamp =
    `${timestamp.getFullYear()}` +
    `${padTwo(timestamp.getMonth() + 1)}` +
    `${padTwo(timestamp.getDate())}-` +
    `${padTwo(timestamp.getHours())}` +
    `${padTwo(timestamp.getMinutes())}` +
    `${padTwo(timestamp.getSeconds())}`;
  return join(LOGS_DIR, `${stamp}-merger-dirt.patch`);
}

/**
 * Find sandcastle merger worktrees that still have uncommitted changes.
 *
 * The upstream @ai-hero/sandcastle merge-back to HEAD carries ONLY COMMITS.
 * If the merger agent leaves tracked changes behind (conflict resolutions,
 * gate fixes it forgot to commit, etc.), those changes are silently discarded
 * and HEAD differs from the tree the merger validated. A round whose conflict
 * resolutions were silently dropped this way is the exact failure mode this guards.
 *
 * FAIL CLOSED: every scan step that errors lands in `scanErrors` instead of
 * being swallowed — with one deliberate exception: a worktree whose directory
 * is GONE from disk was legitimately removed between listing and scanning, and
 * skipping it is the correct verdict, not a failure.
 */
export function findDirtyMergerWorktrees(
  repoRoot: string,
  exec: GitExec = defaultGitExec,
  presence: PathPresenceProbe = defaultPathPresence,
): { dirty: { worktreePath: string; statusLines: string[] }[]; scanErrors: string[] } {
  let porcelain: string;
  try {
    porcelain = exec(["worktree", "list", "--porcelain"], repoRoot);
  } catch (err) {
    // Listing failed: nothing was verified. This must surface as a scan error,
    // never as an empty (clean-looking) result.
    return {
      dirty: [],
      scanErrors: [`git worktree list failed in ${repoRoot}: ${errorMessage(err)}`],
    };
  }

  const worktrees: string[] = [];
  for (const line of porcelain.split(/\r?\n/)) {
    if (line.startsWith("worktree ")) {
      const path = line.slice("worktree ".length);
      if (path.includes("/.sandcastle/worktrees/sandcastle-merger-")) {
        worktrees.push(path);
      }
    }
  }

  const dirty: { worktreePath: string; statusLines: string[] }[] = [];
  const scanErrors: string[] = [];
  for (const worktreePath of worktrees) {
    let status: string;
    try {
      status = exec(["status", "--porcelain"], worktreePath);
    } catch (err) {
      if (presence(worktreePath) === "absent") {
        // PROVEN removed (ENOENT) between listing and scanning — nothing to
        // verify. "unknown" deliberately falls through to the error below:
        // an unstattable worktree is not a removed one.
        continue;
      }
      // The worktree exists (or its presence could not even be determined) but
      // it could not be inspected: an unverifiable worktree may be hiding
      // exactly the dirt this scan exists to find.
      scanErrors.push(`git status failed in ${worktreePath}: ${errorMessage(err)}`);
      continue;
    }
    const statusLines = status.split(/\r?\n/).filter((l) => l.length > 0);
    if (statusLines.length > 0) {
      dirty.push({ worktreePath, statusLines });
    }
  }

  return { dirty, scanErrors };
}

/**
 * Capture the uncommitted diffs from every dirty merger worktree into a single
 * patch file, HEAD-relative so staged-but-uncommitted changes are included.
 *
 * Untracked files are reported in statusLines but have no diff to capture; a
 * worktree with ONLY untracked files gets patchPath null. A diff capture that
 * FAILS while the worktree still exists sets the report's `diffError` instead —
 * the worktree is then the only copy of the changes, and the error block warns
 * against removing it.
 */
export function preserveMergerDirt(
  repoRoot: string,
  exec: GitExec = defaultGitExec,
  patchPath: string = makeMergerDirtPatchPath(),
  presence: PathPresenceProbe = defaultPathPresence,
): MergerDirtScan {
  const { dirty, scanErrors } = findDirtyMergerWorktrees(repoRoot, exec, presence);
  if (dirty.length === 0) {
    return { reports: [], scanErrors };
  }

  const reports: DirtyWorktreeReport[] = [];

  for (const { worktreePath, statusLines } of dirty) {
    let diff: string;
    try {
      // `diff HEAD`, not bare `diff`: an agent that staged its fixes (`git add`
      // without commit) is dirty in status but INVISIBLE to a bare `git diff` —
      // the preserved patch would be empty exactly when the work looked most
      // deliberate. HEAD-relative captures staged and unstaged alike.
      diff = exec(["diff", "HEAD"], worktreePath);
    } catch (err) {
      if (presence(worktreePath) === "absent") {
        // PROVEN removed (ENOENT) mid-scan after the dirty status read: the
        // changes are gone with it and there is nothing left to preserve or
        // protect. Still report what the status saw, so the operator knows
        // work MAY have been lost.
        reports.push({
          worktreePath,
          statusLines,
          patchPath: null,
          diffError: "worktree was removed before its diff could be captured",
        });
        continue;
      }
      // The worktree still exists — or its presence could not even be
      // determined, which must be treated the same — and its diff could not be
      // read: it is now the ONLY copy of the changes. Never let this look like
      // "nothing to capture" or, worse, "removed".
      reports.push({
        worktreePath,
        statusLines,
        patchPath: null,
        diffError: errorMessage(err),
      });
      continue;
    }

    const diffTrimmed = diff.trim();
    if (diffTrimmed.length === 0) {
      // Only untracked files — visible in statusLines, nothing HEAD-relative to save.
      reports.push({ worktreePath, statusLines, patchPath: null, diffError: null });
      continue;
    }

    try {
      mkdirSync(dirname(patchPath), { recursive: true });
      appendFileSync(patchPath, `--- merger-dirt: ${worktreePath}\n${diff}\n`, "utf8");
      reports.push({ worktreePath, statusLines, patchPath, diffError: null });
    } catch (err) {
      // The diff was READ but could not be WRITTEN (full disk, unwritable logs
      // dir). The worktree still holds the changes — same posture as a failed
      // capture: point at the worktree, do not claim a patch that isn't there.
      reports.push({
        worktreePath,
        statusLines,
        patchPath: null,
        diffError: `captured diff could not be written to ${patchPath}: ${errorMessage(err)}`,
      });
    }
  }

  return { reports, scanErrors };
}

/**
 * Render a loud error block naming the dirty worktree(s), their dirty files,
 * the patch path, and recovery instructions. Reports with a diffError warn
 * that the worktree is the only remaining copy of the changes.
 */
export function mergerDirtErrorBlock(reports: DirtyWorktreeReport[]): string {
  const lines: string[] = [
    "",
    "!!! MERGER LEFT UNCOMMITTED WORK — HEAD IS NOT WHAT THE MERGER VALIDATED !!!",
    "",
    "The upstream merge-back carries only commits. The merger worktree below has " +
    "uncommitted changes, so the tree that passed validation differs from HEAD. " +
    "The post-merge gate was NOT run.",
    "",
  ];

  for (const report of reports) {
    lines.push(`Worktree: ${report.worktreePath}`);
    lines.push("Dirty files:");
    for (const statusLine of report.statusLines) {
      lines.push(`  ${statusLine}`);
    }
    if (report.patchPath !== null) {
      lines.push(`Preserved diff: ${report.patchPath}`);
    } else if (report.diffError !== null) {
      lines.push(
        `Diff capture FAILED (${report.diffError}) — the worktree is the ONLY copy ` +
        "of these changes. Inspect it directly and do NOT remove it until the work " +
        "is recovered.",
      );
    } else {
      lines.push(
        "No tracked diff was captured (only untracked files). Inspect the worktree directly.",
      );
    }
    lines.push("");
  }

  lines.push("Recovery:");
  lines.push(
    "  1. Review the preserved patch above, or inspect the worktree directly.",
  );
  lines.push(
    "  2. Apply the changes to the target branch: `git apply <patch-path>` (or " +
    "manually port them from the worktree).",
  );
  lines.push(
    "  3. Verify the touched areas (typecheck / tests for the files changed).",
  );
  lines.push("  4. Commit the result.");
  lines.push(
    "  5. Only after the work is recovered and committed, remove the stale " +
    "worktree: `git worktree remove --force <worktree>`.",
  );
  lines.push("  6. Rerun Sandcastle.");
  lines.push("");

  return lines.join("\n");
}

/**
 * Render the fail-closed block for a scan that could not complete. Deliberately
 * distinct from mergerDirtErrorBlock: this does NOT claim dirt was found — it
 * says the orchestrator could not PROVE the merger worktrees are clean, and
 * refuses to continue on that basis (the gate's COULD-NOT-VERIFY posture).
 */
export function mergerDirtScanErrorBlock(scanErrors: string[]): string {
  const lines: string[] = [
    "",
    "!!! COULD NOT VERIFY THE MERGER WORKTREES ARE CLEAN !!!",
    "",
    "The dirty-worktree scan itself failed, so uncommitted merger work may exist " +
    "that was NOT detected. Refusing to continue on an unverified HEAD. " +
    "The post-merge gate was NOT run.",
    "",
    "Scan failures:",
  ];
  for (const scanError of scanErrors) {
    lines.push(`  ${scanError}`);
  }
  lines.push("");
  lines.push(
    "Inspect the worktrees under .sandcastle/worktrees/ manually (git status in " +
    "each sandcastle-merger-* one), recover and commit anything uncommitted, then " +
    "rerun Sandcastle.",
  );
  lines.push("");
  return lines.join("\n");
}
