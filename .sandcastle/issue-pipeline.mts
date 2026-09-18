// Issue pipeline — implementer → reviewer, and which branches reach the merge.
//
// Extracted from main.mts so the "work must never be stranded" rules below are
// testable without Docker, a model, or a real git repo.
//
// The rules exist because of a real incident: the implementer committed twice
// and Sandcastle synced both commits to the host branch, then the reviewer died
// on a too-large prompt. The reviewer's throw rejected the whole issue
// pipeline, so the branch never reached the merge phase and the issue was never
// closed — and the next planning round re-picked the same issue, re-ran it, and
// failed the same way. Every round after that produced errors and no merge
// until a human intervened.
//
// Two guards, split along the line of what has actually been validated:
//   1. A reviewer failure is caught, not propagated — the implementer already
//      finished and ran this repo's gates, so its commits are sound and must
//      still be merged. (The common case, and the incident case.)
//   2. A branch left ahead of the merge target by an earlier round is picked up
//      by asking git — but ONLY when this round's pipeline completed. Commits
//      behind a pipeline that THREW are reported, never auto-merged: a crashed
//      implementer can leave half-finished, never-gated work, and merging that
//      lands a broken tree on the host branch, which is the red HEAD this
//      whole mechanism exists to avoid.

export type PipelinePhaseResult = { commits: readonly unknown[] };

export type IssuePipelineOutcome = {
  /** Commits from both phases, in run order. */
  commits: readonly unknown[];
  /** Set when the reviewer failed and its error was absorbed. */
  reviewFailure?: unknown;
  /**
   * The branch marker captured once the implementer's gated work was on the
   * branch, reported only alongside a reviewFailure — that is the only case
   * where the caller has to prove the branch carries nothing beyond it.
   * Absent means it could not be captured, which the caller must treat as
   * "cannot prove this branch is gated".
   */
  validatedTip?: string;
};

export type IssuePipelineHooks = {
  runImplementer: () => Promise<PipelinePhaseResult>;
  runReviewer: () => Promise<PipelinePhaseResult>;
  /**
   * Called once the implementer has finished and its gated commits are on the
   * branch, BEFORE the reviewer can add anything. Returns a marker for that
   * known-validated branch state (null when it cannot be determined), so a
   * reviewer that dies mid-gates cannot leave ungated commits behind it.
   */
  captureValidated?: () => string | null;
  /**
   * Called when the reviewer throws. Absorbing the error keeps the
   * implementer's commits mergeable; re-throwing from here aborts the pipeline
   * (used for non-recoverable auth failures that every later phase would hit).
   */
  onReviewFailure?: (error: unknown) => void;
};

/**
 * Run the implementer, then the reviewer if the implementer produced commits.
 *
 * A reviewer failure NEVER discards the implementer's commits — they are
 * already committed on the branch and synced to the host.
 */
export async function runIssuePipeline(
  hooks: IssuePipelineHooks,
): Promise<IssuePipelineOutcome> {
  const implement = await hooks.runImplementer();

  if (implement.commits.length === 0) return { commits: implement.commits };

  const validatedTip = hooks.captureValidated?.() ?? undefined;

  try {
    const review = await hooks.runReviewer();
    return { commits: [...implement.commits, ...review.commits] };
  } catch (error) {
    // May re-throw (fatal auth) — that is the caller's deliberate choice.
    hooks.onReviewFailure?.(error);
    return { commits: implement.commits, reviewFailure: error, validatedTip };
  }
}

export type MergeableSelection<TIssue> = {
  /** Issues whose branch has work that is safe to merge this round. */
  mergeable: TIssue[];
  /**
   * Issues whose pipeline ran to completion but reported no commits, while
   * their branch is ahead of the merge target — work an earlier round left
   * behind. Merged, and worth logging: the old accounting lost these.
   */
  rescued: TIssue[];
  /**
   * Issues whose pipeline FAILED while their branch holds commits. NOT merged
   * — a crashed implementer can leave half-finished, never-gated commits, and
   * merging those lands a broken tree on the host branch (which is exactly the
   * red-HEAD state this whole guard exists to prevent). Report them and let a
   * human decide.
   */
  stranded: TIssue[];
};

/**
 * Decide which planned issues reach the merge phase.
 *
 * Merging is gated on the pipeline having COMPLETED, because completion is the
 * only evidence the branch was validated: the implementer runs this repo's
 * gates before it finishes. A branch is therefore mergeable when
 *   - its pipeline returned commits, or
 *   - its pipeline finished cleanly and git says the branch is still ahead of
 *     the merge target (work stranded by an earlier broken round).
 * A branch whose pipeline threw is never merged on the strength of git alone.
 */
export function selectMergeableIssues<TIssue extends { branch: string }>(
  issues: readonly TIssue[],
  settled: readonly PromiseSettledResult<PipelinePhaseResult>[],
  commitsAhead: (branch: string) => number,
): MergeableSelection<TIssue> {
  const mergeable: TIssue[] = [];
  const rescued: TIssue[] = [];
  const stranded: TIssue[] = [];

  for (const [index, issue] of issues.entries()) {
    const outcome = settled[index];

    if (outcome?.status === "fulfilled" && outcome.value.commits.length > 0) {
      mergeable.push(issue);
      continue;
    }

    if (commitsAhead(issue.branch) === 0) continue;

    if (outcome?.status === "fulfilled") {
      mergeable.push(issue);
      rescued.push(issue);
    } else {
      stranded.push(issue);
    }
  }

  return { mergeable, rescued, stranded };
}

export type LandedSelection<TIssue> = {
  /** Issues whose branch is provably contained in the host HEAD. */
  landed: TIssue[];
  /**
   * Issues sent to the merger whose branch is NOT in HEAD. Their work did not
   * land, so they must stay open for the next round to re-pick.
   */
  unlanded: TIssue[];
};

/**
 * Split the merged round into what actually reached HEAD and what did not.
 *
 * The orchestrator closes a round's issues from the host, and `merge-prompt.md`
 * promises the agent that this happens "only after the merge has really
 * landed". Nothing enforced that promise: the merger is asked to merge
 * "everything you can" and stops at `maxIterations`, and a Sandcastle run that
 * hits its iteration cap without a completion signal RESOLVES — it does not
 * throw. So a merger that landed three of five branches and ran out of
 * iterations returned success, and every issue in the round was closed,
 * including the two whose work never merged. A closed issue is invisible to
 * the planner (`gh issue list --state open`), so that work left the backlog
 * silently and permanently.
 *
 * The check is ancestry in the host repo, not anything the agent reports:
 * merge-prompt.md merges with `git merge <branch> --no-edit`, so a branch that
 * landed — fast-forward, merge commit, or conflict resolution — is an ancestor
 * of HEAD, and one that did not is not.
 *
 * A deeper planner queue makes this far likelier by putting more branches in
 * front of the same two-iteration merger, but the hole predates it: closing on
 * the strength of "the merger returned" was never sound.
 */
export function partitionLandedIssues<TIssue extends { branch: string }>(
  issues: readonly TIssue[],
  landedInHead: (branch: string) => boolean,
): LandedSelection<TIssue> {
  const landed: TIssue[] = [];
  const unlanded: TIssue[] = [];

  for (const issue of issues) {
    // Fail closed: an ancestry check that cannot answer must leave the issue
    // open. A wrongly-open issue costs one duplicate round; a wrongly-closed
    // one loses the work.
    (landedInHead(issue.branch) ? landed : unlanded).push(issue);
  }

  return { landed, unlanded };
}
