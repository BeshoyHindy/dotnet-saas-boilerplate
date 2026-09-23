// Parallel Planner with Review — four-phase orchestration loop
//
//   Phase 1 (Plan)    A planner agent reads the open issues, builds a
//                     dependency graph, and emits a <plan> JSON of unblocked
//                     issues with branch names.
//   Phase 2 (Execute) Each issue gets its own sandbox: the implementer runs
//                     first, then — if it committed — a reviewer on the same
//                     branch. Pipelines run concurrently, capped at the
//                     concurrency limit, drawing from the planner's deeper
//                     queue so a freed slot starts the next issue immediately.
//   Phase 3 (Merge)   One agent merges every completed branch into the current
//                     branch; the host then runs the post-merge gates on HEAD.
//   Phase 4 (Heal)    If those gates deliver a RED verdict, a healer agent runs
//                     ON THE HOST (Docker lives there, so it can run the
//                     container-backed suites) in a merge-to-head worktree; its
//                     commits merge back and the gates re-run — up to
//                     `limits.healAttempts` times before the run stops as red.
//                     Healing is OFF by default. An UNVERIFIED gate (Docker
//                     died, timeout) or a Ctrl+C is never "healed".
//
// The outer loop repeats up to `limits.maxIterations` times so that newly
// unblocked issues are picked up after each round of merges.
//
// Every phase runs in a git worktree (merge-to-head or an explicit branch) —
// NEVER the host working directory — so untracked/gitignored files such as
// .env and .sandcastle/.env physically do not exist inside any sandbox.
//
// EVERYTHING repo-specific lives in `sandcastle.config.mts` at the repo root.
// This file is the only module that imports it; the logic modules take what
// they need as arguments, which is what keeps them unit-testable.
//
// Usage:
//   pnpm sandcastle                (launch from the integration branch)
//   pnpm sandcastle --dry-run      (print the resolved plan; start nothing)
//   pnpm sandcastle:build-image    (build/rebuild the sandbox Docker image)

import * as sandcastle from "@ai-hero/sandcastle";
import { docker } from "@ai-hero/sandcastle/sandboxes/docker";
import { noSandbox } from "@ai-hero/sandcastle/sandboxes/no-sandbox";
import { z } from "zod";
import { execFileSync } from "node:child_process";
import {
  closeSync,
  existsSync,
  fsyncSync,
  mkdirSync,
  openSync,
  readFileSync,
  renameSync,
  rmSync,
  writeSync,
} from "node:fs";
import { homedir } from "node:os";
import { dirname, isAbsolute, join } from "node:path";

import config from "../sandcastle.config.mts";
import {
  formatGateCommands,
  gateNames,
  joinGateCommands,
  resolveLimits,
  resolveModels,
  type PhaseModel,
} from "./config.mts";
import { branchTip, gitIn, restoreValidatedBranch } from "./branch-guard.mts";
import { mapWithConcurrency } from "./agent-pool.mts";
import {
  mergeLedger,
  parseLedger,
  partitionLedger,
  remainingLedger,
  serializeLedger,
  type PendingClose,
} from "./close-ledger.mts";
import {
  isDryRun,
  listAgentIssues,
  renderDryRun,
  type CommandExec,
} from "./dry-run.mts";
import {
  partitionLandedIssues,
  runIssuePipeline,
  selectMergeableIssues,
} from "./issue-pipeline.mts";
import {
  readGateExcerpt,
  readGateFailures,
  runPostMergeGates,
  writeStderrSync,
} from "./post-merge-gate.mts";
import { runGateWithHealing, type HealAttemptInput } from "./gate-heal.mts";
import {
  mergerDirtErrorBlock,
  mergerDirtScanErrorBlock,
  preserveMergerDirt,
} from "./merger-dirt.mts";

// ---------------------------------------------------------------------------
// Resolved configuration
// ---------------------------------------------------------------------------

// Environment overrides (MAX_CONCURRENT_AGENTS, SANDCASTLE_HEAL_ATTEMPTS, and
// the per-phase SANDCASTLE_<PHASE>_MODEL / SANDCASTLE_<PHASE>_EFFORT) are
// applied here and nowhere else; a malformed one THROWS rather than silently
// restoring a default that may not fit this machine or this run.
const limits = resolveLimits(config.limits, process.env);
const models = resolveModels(config.models, process.env);
// The one channel by which a prompt learns a gate command — see config.mts.
const GATE_COMMANDS = formatGateCommands(config.gates);

// ---------------------------------------------------------------------------
// Dry run — resolve everything, acquire nothing.
//
// Runs BEFORE the sentinel is bound, so it is safe alongside a live run, and
// before any sandbox or model session exists. A config mistake (wrong label,
// wrong branch, a gate whose binary is misspelt) becomes visible in a second
// instead of forty minutes into a round.
// ---------------------------------------------------------------------------
if (isDryRun(process.argv.slice(2), process.env)) {
  const exec: CommandExec = (file, args) =>
    execFileSync(file, [...args], {
      encoding: "utf8",
      stdio: ["ignore", "pipe", "pipe"],
    });
  console.log(
    renderDryRun(
      config,
      limits,
      models,
      listAgentIssues(config.issues.listArgs, exec),
    ),
  );
  process.exit(0);
}

// ---------------------------------------------------------------------------
// Singleton sentinel — one sandcastle orchestrator per machine, full stop.
//
// Two concurrent orchestrators race each other on the SAME issue branches,
// worktrees and merge-to-HEAD syncs: duplicate planners pick the same issues,
// and the loser of each race dies with exit-128 git errors while the winner
// merges. An exclusive loopback bind is atomic in the OS, held for the
// process's whole lifetime and self-releasing on any death — nothing to go
// stale.
// ---------------------------------------------------------------------------
await (async () => {
  const net = await import("node:net");
  // No protocol — destroy inbound connections so a stray probe can never hold
  // the sentinel's event loop hostage.
  const server = net.createServer((socket) => {
    socket.destroy();
  });
  await new Promise<void>((resolve, reject) => {
    server.once("error", (error: NodeJS.ErrnoException) => {
      reject(
        error.code === "EADDRINUSE"
          ? new Error(
            `Another sandcastle orchestrator is already running (sentinel port ${config.sandbox.sentinelPort} is taken). ` +
            "Two orchestrators race each other on the same issue branches and merge-to-HEAD syncs — " +
            "let the running one finish, or stop it first.",
          )
          : error,
      );
    });
    server.listen(config.sandbox.sentinelPort, "127.0.0.1", () => resolve());
  });
  // Held for the whole run; must not keep the event loop alive by itself.
  server.unref();
})();

// The planner emits its plan as JSON inside <plan> tags; Output.object extracts
// and validates it against this schema.
const planSchema = z.object({
  issues: z.array(
    z.object({ id: z.string(), title: z.string(), branch: z.string() }),
  ),
});

type PlanIssue = z.infer<typeof planSchema>["issues"][number];

type ClaudeCodeOptions = NonNullable<Parameters<typeof sandcastle.claudeCode>[1]>;
type ClaudeCodeExtras = Omit<ClaudeCodeOptions, "effort">;

// Route a phase's model to its agent CLI, so swapping models is a config edit
// and never a call-site edit. `extras` carries per-call CLI options (the
// healer's permission mode).
const agentFor = (
  { model, effort }: PhaseModel,
  extras: ClaudeCodeExtras = {},
) => {
  if (model.startsWith("claude-")) {
    return sandcastle.claudeCode(model, {
      effort: effort as ClaudeCodeOptions["effort"],
      ...extras,
    });
  }

  throw new Error(`No Sandcastle agent provider is configured for "${model}"`);
};

// Shared host-side caches, bind-mounted into every sandbox so restores and
// installs are warm after the first round. Package caches tolerate concurrent
// readers and writers, which is what makes one shared copy safe across agents.
const CACHE_ROOT = config.sandbox.cacheRoot.startsWith("~")
  ? join(homedir(), config.sandbox.cacheRoot.slice(1))
  : config.sandbox.cacheRoot;
const MOUNTS = config.sandbox.caches.map((cache) => ({
  hostPath: isAbsolute(cache.hostDir) ? cache.hostDir : join(CACHE_ROOT, cache.hostDir),
  sandboxPath: cache.sandboxPath,
}));
for (const mount of MOUNTS) mkdirSync(mount.hostPath, { recursive: true });

// Single source of truth for sandbox configuration — used by every phase.
const makeSandbox = () =>
  docker({ mounts: MOUNTS, env: { ...config.sandbox.env } });

// Close merged issues from the HOST after the merge phase. The merger agent is
// also told to leave them open precisely because this is the deterministic
// backstop; failures here are logged, never fatal. Returns the ids that ended
// up closed (including "was already closed") so the ledger can retire exactly
// those and retry the rest next startup.
function closeMergedIssues(issues: ReadonlyArray<Pick<PlanIssue, "id">>): Set<string> {
  const closed = new Set<string>();
  for (const issue of issues) {
    try {
      execFileSync(
        "gh",
        ["issue", "close", issue.id, "--comment", config.issues.closeComment],
        { stdio: "pipe" },
      );
      closed.add(issue.id);
      console.log(`  Closed issue #${issue.id}`);
    } catch (error) {
      // gh close errors on an ALREADY-closed issue — that outcome is success
      // for our purposes, so ask before treating it as a failure to retry.
      if (issueIsClosed(issue.id)) {
        closed.add(issue.id);
        console.log(`  Issue #${issue.id} was already closed`);
      } else {
        console.warn(`  Could not close issue #${issue.id}: ${error}`);
      }
    }
  }
  return closed;
}

// True only when GitHub positively reports the issue CLOSED. Fails closed:
// any gh error answers "not closed", which keeps the ledger entry for retry.
function issueIsClosed(id: string): boolean {
  try {
    const state = execFileSync(
      "gh",
      ["issue", "view", id, "--json", "state", "-q", ".state"],
      { encoding: "utf8", stdio: ["ignore", "pipe", "pipe"] },
    ).trim();
    return state === "CLOSED";
  } catch {
    return false;
  }
}

// The end-of-round merge-back runs `git merge` in THIS repo, and git refuses it
// on an unresolved merge in progress (MERGE_HEAD exists) or uncommitted changes
// to tracked files — either of which would otherwise surface only at the very
// end, after a full round of agent time is already spent. Refuse to start
// instead. Untracked files are fine: merges tolerate them.
function hostRepoBlocksMergeBack(): string | null {
  try {
    execFileSync("git", ["rev-parse", "--quiet", "--verify", "MERGE_HEAD"], {
      stdio: "pipe",
    });
    return (
      "an unresolved merge is in progress (MERGE_HEAD exists). Resolve the " +
      "conflicts and `git commit`, or `git merge --abort`."
    );
  } catch {
    // No merge in progress — keep checking.
  }
  const dirty = execFileSync(
    "git",
    ["status", "--porcelain", "--untracked-files=no"],
    { encoding: "utf8" },
  ).trim();
  if (dirty.length > 0) {
    return (
      "tracked files have uncommitted changes — commit or stash them " +
      `first:\n${dirty}`
    );
  }
  return null;
}

// How many commits `branch` has that HEAD does not — 0 when the branch does
// not exist or git fails. This is the source of truth for "is there work to
// merge", because every phase syncs its commits to the host branch as it ends:
// a branch can be ahead of HEAD even when the run that produced it died (see
// issue-pipeline.mts).
function commitsAheadOfHead(branch: string): number {
  try {
    const count = execFileSync("git", ["rev-list", "--count", `HEAD..${branch}`], {
      encoding: "utf8",
      stdio: ["ignore", "pipe", "pipe"],
    }).trim();
    return Number.parseInt(count, 10) || 0;
  } catch {
    return 0;
  }
}

// Git against the host repo, for the branch guard (see branch-guard.mts).
const hostGit = gitIn(process.cwd());

// True when `branch` is provably contained in the host HEAD. Fails closed:
// any git error answers "not landed", which keeps the issue open rather than
// closing work that may never have merged.
function branchLandedInHead(branch: string): boolean {
  try {
    execFileSync("git", ["merge-base", "--is-ancestor", branch, "HEAD"], {
      stdio: ["ignore", "ignore", "pipe"],
    });
    return true;
  } catch {
    return false;
  }
}

// ---------------------------------------------------------------------------
// Durable close ledger (rules + rationale: close-ledger.mts)
// ---------------------------------------------------------------------------
//
// Written before every merger run, reconciled at every startup: any exit —
// clean, thrown, or SIGKILLed — leaves enough durable state for the next run to
// close exactly the issues whose recorded work is in HEAD, and nothing else.
// That also makes MANUAL recovery safe: merge a preserved temp branch, rerun,
// and this sweep closes the landed issues before the planner re-picks them.
const PENDING_CLOSE_LEDGER = ".sandcastle/pending-close.json";

function readLedger(): PendingClose[] {
  try {
    return parseLedger(
      existsSync(PENDING_CLOSE_LEDGER)
        ? readFileSync(PENDING_CLOSE_LEDGER, "utf8")
        : null,
    );
  } catch {
    return [];
  }
}

function persistLedger(entries: readonly PendingClose[]): void {
  if (entries.length === 0) {
    rmSync(PENDING_CLOSE_LEDGER, { force: true });
    return;
  }
  // The full power-loss dance, not just write-then-rename: fsync the staging
  // file BEFORE the rename (or the rename can become durable pointing at
  // never-flushed bytes), then fsync the parent directory AFTER it (or the
  // rename can be rolled back by a power cut). A torn ledger parses as empty
  // and would silently drop every entry.
  const staging = `${PENDING_CLOSE_LEDGER}.tmp`;
  const fd = openSync(staging, "w");
  try {
    writeSync(fd, serializeLedger(entries));
    fsyncSync(fd);
  } finally {
    closeSync(fd);
  }
  renameSync(staging, PENDING_CLOSE_LEDGER);
  const dirFd = openSync(dirname(PENDING_CLOSE_LEDGER), "r");
  try {
    fsyncSync(dirFd);
  } finally {
    closeSync(dirFd);
  }
}

// The branch's current commit, or null when it cannot be resolved (deleted
// branch, git failure) — a null tip is simply not recorded.
function tipOf(branch: string): string | null {
  try {
    return execFileSync("git", ["rev-parse", "--verify", `${branch}^{commit}`], {
      encoding: "utf8",
      stdio: ["ignore", "pipe", "pipe"],
    }).trim();
  } catch {
    return null;
  }
}

// branchLandedInHead's twin for an exact commit. Fails closed the same way.
function commitLandedInHead(commit: string): boolean {
  try {
    execFileSync("git", ["merge-base", "--is-ancestor", commit, "HEAD"], {
      stdio: ["ignore", "ignore", "pipe"],
    });
    return true;
  } catch {
    return false;
  }
}

function reconcilePendingCloses(): void {
  const pending = readLedger();
  if (pending.length === 0) {
    return;
  }
  const { landed, waiting } = partitionLedger(pending, commitLandedInHead);
  let closed = new Set<string>();
  if (landed.length > 0) {
    console.log(
      `Close ledger: ${landed.length} issue(s) whose recorded work is in HEAD ` +
      "but which no earlier run closed — closing before planning:",
    );
    closed = closeMergedIssues(landed);
  }
  if (waiting.length > 0) {
    console.log(
      `Close ledger: ${waiting.length} issue(s) still waiting for their ` +
      "recorded work to land; keeping them recorded.",
    );
  }
  persistLedger(remainingLedger(pending, closed));
}

// ---------------------------------------------------------------------------
// Main loop
// ---------------------------------------------------------------------------

// Before any planning: close what a previous run merged but never closed. The
// planner re-plans any open issue, so a merged-but-open issue becomes a
// duplicate round — this MUST run first.
reconcilePendingCloses();

for (let iteration = 1; iteration <= limits.maxIterations; iteration++) {
  console.log(`\n=== Iteration ${iteration}/${limits.maxIterations} ===\n`);

  const blocked = hostRepoBlocksMergeBack();
  if (blocked !== null) {
    console.error(
      `Refusing to start: ${blocked}\n` +
      "A dirty host repo makes the end-of-round merge-back fail after " +
      "all the agent work is already done. Clean up, then rerun.",
    );
    process.exit(1);
  }

  // -------------------------------------------------------------------------
  // Phase 1: Plan — up to `limits.plannerQueueDepth` currently-unblocked
  // issues, in priority order. merge-to-head runs the planner in a fresh
  // worktree of tracked files only, never the host working dir, so .env stays
  // out.
  // -------------------------------------------------------------------------
  const plan = await sandcastle.run({
    sandbox: makeSandbox(),
    branchStrategy: { type: "merge-to-head" },
    name: "planner",
    // One iteration is enough: the planner just needs to read and reason,
    // not write code. (Structured output requires maxIterations: 1.)
    maxIterations: 1,
    agent: agentFor(models.planner),
    promptFile: config.prompts.plan,
    promptArgs: {
      PROJECT_NAME: config.project.name,
      ISSUE_LABEL: config.issues.label,
      BRANCH_PREFIX: config.git.branchPrefix,
      // Substituted INTO the prompt's shell block, which the library then
      // executes — only blocks present in the file are ever run, so this
      // cannot smuggle in a second command.
      ISSUE_LIST_COMMAND: config.issues.plannerListCommand,
      // The queue depth, NOT the concurrency cap, despite the arg name.
      MAX_PARALLEL: String(limits.plannerQueueDepth),
    },
    // Extract and validate the <plan> JSON into a typed object. Throws
    // StructuredOutputError if the tag is missing, the JSON is malformed, or
    // validation fails — which aborts the loop.
    output: sandcastle.Output.object({ tag: "plan", schema: planSchema }),
  });

  const issues = plan.output.issues;

  if (issues.length === 0) {
    // No unblocked work — either everything is done or everything is blocked.
    console.log("No unblocked issues to work on. Exiting.");
    break;
  }

  console.log(
    `Planning complete. ${issues.length} issue(s) to work in parallel:`,
  );
  for (const issue of issues) {
    console.log(`  ${issue.id}: ${issue.title} → ${issue.branch}`);
  }

  // -------------------------------------------------------------------------
  // Phase 2: Execute + Review — one sandbox per issue, shared by the
  // implementer and the reviewer so both work the same branch. Like
  // Promise.allSettled, one failing pipeline never cancels the others. The
  // round is a barrier: the merge phase, and therefore the next planning round,
  // waits for the whole queue to drain.
  // -------------------------------------------------------------------------

  const settled = await mapWithConcurrency(
    issues,
    limits.maxConcurrentAgents,
    async (issue: PlanIssue) => {
      const sandbox = await sandcastle.createSandbox({
        branch: issue.branch,
        sandbox: makeSandbox(),
      });

      let outcome: Awaited<ReturnType<typeof runIssuePipeline>>;

      try {
        // The reviewer is a refinement pass, not a gate: if it dies, the
        // implementer's commits are already on the branch and must still be
        // merged, so its error must not reject this pipeline.
        outcome = await runIssuePipeline({
          runImplementer: () =>
            sandbox.run({
              name: "implementer",
              maxIterations: 100,
              idleTimeoutSeconds: limits.idleTimeoutSeconds,
              agent: agentFor(models.implementer),
              promptFile: config.prompts.implement,
              promptArgs: {
                TASK_ID: issue.id,
                ISSUE_TITLE: issue.title,
                BRANCH: issue.branch,
                PROJECT_NAME: config.project.name,
                GATE_COMMANDS,
              },
            }),
          runReviewer: () =>
            sandbox.run({
              name: "reviewer",
              // 3, not 1: with a single iteration the reviewer is cut off
              // mid-gates and its uncommitted refactors are discarded when the
              // sandbox closes. The extra iterations let it commit, then finish
              // and observe the gate result.
              maxIterations: 3,
              idleTimeoutSeconds: limits.idleTimeoutSeconds,
              agent: agentFor(models.reviewer),
              promptFile: config.prompts.review,
              promptArgs: {
                TASK_ID: issue.id,
                ISSUE_TITLE: issue.title,
                BRANCH: issue.branch,
                GATE_COMMANDS,
              },
            }),
          captureValidated: () => branchTip(hostGit, issue.branch),
          onReviewFailure: (error) => {
            console.error(
              `  ✗ ${issue.id} (${issue.branch}) review phase failed: ${error}\n` +
              "    Keeping the implementer's gated commits; the branch merges " +
              "only if it can be shown to carry nothing beyond them.",
            );
          },
        });
      } finally {
        await sandbox.close();
      }

      // The sandbox is gone, so the branch ref can move again. The guard must
      // PROVE the branch holds only gated work; when it cannot, this pipeline
      // fails, routing the branch to the "stranded" report instead of the merge.
      if (outcome.reviewFailure !== undefined) {
        const tip = outcome.validatedTip;
        if (tip === undefined) {
          throw new Error(
            `The reviewer failed and the implementer's validated tip was never ` +
            `recorded, so ${issue.branch} cannot be shown to hold only gated ` +
            "work. Refusing to merge it.",
          );
        }
        const restore = restoreValidatedBranch(
          hostGit,
          issue.id,
          issue.branch,
          tip,
        );
        if (!restore.restored) {
          throw new Error(
            `${issue.branch} could not be rewound to the implementer's validated ` +
            `tip, so it may carry reviewer commits no gate passed (${restore.reason}). ` +
            "Refusing to merge it.",
          );
        }
        if (restore.rescueRef !== undefined) {
          console.warn(
            `  ! ${issue.id} (${issue.branch}) the failed reviewer left commits ` +
            "that no gate passed. Rewound the branch to the implementer's " +
            `validated tip ${tip.slice(0, 7)}; the discarded work is ` +
            `kept on ${restore.rescueRef}.`,
          );
        }
      }

      return outcome;
    },
  );

  // Log any agents that threw (network error, sandbox crash, etc.).
  for (const [i, outcome] of settled.entries()) {
    if (outcome.status !== "rejected") continue;

    console.error(
      `  ✗ ${issues[i]!.id} (${issues[i]!.branch}) failed: ${outcome.reason}`,
    );
  }

  // Only pass branches that actually have work AND completed to the merge
  // phase. A pipeline that threw may have left half-finished, never-gated
  // commits on its branch; those are reported for a human, never auto-merged.
  const {
    mergeable: completedIssues,
    rescued,
    stranded,
  } = selectMergeableIssues<PlanIssue>(issues, settled, commitsAheadOfHead);

  for (const issue of rescued) {
    console.warn(
      `  ! ${issue.id} (${issue.branch}) reported no commits, but the branch is ` +
      `${commitsAheadOfHead(issue.branch)} commit(s) ahead of HEAD — merging ` +
      "the work an earlier round left behind.",
    );
  }

  for (const issue of stranded) {
    console.error(
      `  ! ${issue.id} (${issue.branch}) FAILED with ` +
      `${commitsAheadOfHead(issue.branch)} commit(s) on its branch. NOT merging ` +
      "them: a failed phase can leave half-finished, ungated work. Inspect with " +
      `\`git log HEAD..${issue.branch}\` and merge deliberately if it is sound. ` +
      "The issue stays open.",
    );
  }

  const completedBranches = completedIssues.map((i) => i.branch);

  console.log(
    `\nExecution complete. ${completedBranches.length} branch(es) with commits:`,
  );
  for (const branch of completedBranches) {
    console.log(`  ${branch}`);
  }

  if (completedBranches.length === 0) {
    // All agents ran but none made commits — nothing to merge this cycle.
    console.log("No commits produced. Nothing to merge.");
    continue;
  }

  // -------------------------------------------------------------------------
  // Phase 3: Merge — one agent merges every completed branch into the current
  // branch, resolving conflicts and running the gates. merge-to-head puts it on
  // a temp branch in a fresh worktree (worktrees share the repo's refs, so the
  // issue branches resolve) and merges the result back into HEAD.
  // -------------------------------------------------------------------------
  // Record the close ledger BEFORE the merger runs: from here on, any exit
  // leaves durable state from which the next startup closes exactly what landed
  // (judged by these recorded tips). MERGED with existing entries, never
  // overwritten — a still-waiting entry from an earlier round must survive.
  persistLedger(mergeLedger(readLedger(), completedIssues.flatMap((issue) => {
    const tip = tipOf(issue.branch);
    return tip === null ? [] : [{ id: issue.id, branch: issue.branch, tip }];
  })));

  try {
    await sandcastle.run({
      sandbox: makeSandbox(),
      branchStrategy: { type: "merge-to-head" },
      name: "merger",
      // 2, not 1: a merger cut off at one iteration merges to HEAD with its
      // gates still running AND never reaches its final report.
      maxIterations: 2,
      idleTimeoutSeconds: limits.idleTimeoutSeconds,
      agent: agentFor(models.merger),
      promptFile: config.prompts.merge,
      promptArgs: {
        // A markdown list of branch names, one per line.
        BRANCHES: completedBranches.map((b) => `- ${b}`).join("\n"),
        // A markdown list of issue IDs and titles, one per line.
        ISSUES: completedIssues.map((i) => `- ${i.id}: ${i.title}`).join("\n"),
        GATE_COMMANDS,
      },
    });
  } catch (error) {
    // Two distinct failures surface here and must not be conflated:
    //   1. SyncError — the temp branch could not be merged back onto HEAD.
    //      Nothing landed; the temp branch named in the error is preserved.
    //   2. WorktreeError — the merge-back LANDED and only the cleanup after it
    //      failed, leaving MERGED issues open.
    // So ask git which branches actually landed and close those NOW — an
    // open-but-merged issue is a duplicate round waiting to happen. Anything
    // unlanded stays open for recovery.
    console.error(`\nMerge-back onto the host branch failed: ${error}`);
    const { landed, unlanded } = partitionLandedIssues(
      completedIssues,
      branchLandedInHead,
    );
    if (landed.length > 0) {
      console.error(
        `\n${landed.length} of ${completedIssues.length} branch(es) DID land ` +
        "in HEAD before the failure (a cleanup-only error). Closing their " +
        "issues so the next round cannot re-plan them:",
      );
      persistLedger(remainingLedger(readLedger(), closeMergedIssues(landed)));
    }
    if (unlanded.length > 0) {
      // The manual path must end the same way the automatic path does: with
      // exactly the LANDED issues closed. A recovered-but-open issue is
      // re-picked by the next planner; a closed-but-never-landed issue drops
      // its work from the backlog for good. The printed commands are each
      // gated on the same git proof branchLandedInHead uses.
      console.error(
        "\nBranches NOT in HEAD — their merged work is preserved on the " +
        "temporary branch named above. Recover with: git merge <temp-branch> " +
        "(resolve conflicts, commit), then simply rerun: the recorded work " +
        "is in the close ledger, and startup closes whatever landed before " +
        "any planning. To close eagerly instead, each command below closes " +
        "its issue only if git proves that branch landed in HEAD:",
      );
      for (const issue of unlanded) {
        console.error(
          `  git merge-base --is-ancestor ${issue.branch} HEAD ` +
          `&& gh issue close ${issue.id}`,
        );
      }
    }
    console.error(
      "\nThe post-merge gate did NOT run. Verify HEAD (" +
      `${joinGateCommands(config.gates)}) before rerunning Sandcastle.`,
    );
    process.exit(1);
  }

  console.log("\nBranches merged.");

  // Deterministic backstop: close the merged issues from the host so the next
  // planning round can never re-pick them. "Merged" means git says so, not that
  // the merger agent returned — a run that hits its iteration cap without a
  // completion signal RESOLVES rather than throwing, so a merger that landed
  // three of five branches looks exactly like total success, and a wrongly
  // closed issue is invisible to the planner for good.
  const { landed, unlanded } = partitionLandedIssues(
    completedIssues,
    branchLandedInHead,
  );

  for (const issue of unlanded) {
    console.error(
      `  ! ${issue.id} (${issue.branch}) was sent to the merger but is NOT in ` +
      "HEAD — the merge did not land (most likely the merger hit its " +
      "iteration cap). Leaving the issue OPEN so the next round re-picks it. " +
      `Inspect with \`git log HEAD..${issue.branch}\`.`,
    );
  }

  // Ledger update rides the close outcome: closed entries retire, a
  // landed-but-close-FAILED entry stays for startup retry, and unlanded entries
  // stay so a later manual merge is still closed by the next run.
  persistLedger(remainingLedger(readLedger(), closeMergedIssues(landed)));

  // Host-side guard: the upstream @ai-hero/sandcastle merge-back carries only
  // commits, so a dirty merger worktree means HEAD differs from the validated
  // tree. Preserve the diff and hard-fail, FAILING CLOSED both ways: scan
  // errors (git itself failing) exit too, and the blocks go out via
  // writeStderrSync, which process.exit() cannot truncate — console.error to a
  // pipe can lose the tail, and the tail here is the recovery instructions.
  const mergerDirtScan = preserveMergerDirt(process.cwd());
  if (mergerDirtScan.reports.length > 0 || mergerDirtScan.scanErrors.length > 0) {
    if (mergerDirtScan.reports.length > 0) {
      writeStderrSync(mergerDirtErrorBlock(mergerDirtScan.reports) + "\n");
    }
    if (mergerDirtScan.scanErrors.length > 0) {
      writeStderrSync(mergerDirtScanErrorBlock(mergerDirtScan.scanErrors) + "\n");
    }
    process.exit(1);
  }

  // Per-branch gates do not compose — individually green branches can still
  // produce a red merged HEAD — so verify the merged whole from the host before
  // another planning round can compound merge-seam failures.
  if (process.env.SANDCASTLE_SKIP_POST_MERGE_GATE === "1") {
    console.warn(
      "\n!!! POST-MERGE GATE SKIPPED " +
      "(SANDCASTLE_SKIP_POST_MERGE_GATE=1) !!!",
    );
    console.warn("The merged HEAD was not verified. Commands not run:");
    for (const gate of config.gates) {
      console.warn(`  [${gate.name}] ${gate.file} ${gate.args.join(" ")}`);
    }
  } else {
    console.log("\nStarting post-merge gate on the merged HEAD:");
    const gateName = gateNames(config.gates);
    const gateCommandLine = joinGateCommands(config.gates);
    const outcome = await runGateWithHealing({
      maxAttempts: limits.healAttempts,
      gateName,
      // Each gate's log is read with the parser THAT gate declared: a build log
      // and a test log carry their failures in completely different shapes.
      failuresOf: (red) =>
        red.logPath === undefined ? [] : readGateFailures(red.logPath, red.parser),
      runGate: (logSuffix) =>
        runPostMergeGates(
          config.gates,
          undefined,
          undefined,
          (gate, logPath) => {
            console.log(`  [${gate.name}] ${gate.file} ${gate.args.join(" ")}`);
            console.log(`  tail -f ${logPath}`);
          },
          logSuffix,
        ),
      onAttempt: ({ attempt, maxAttempts, logPath, failures }: HealAttemptInput) => {
        console.error(
          "\n!!! POST-MERGE GATE FAILED: the merged HEAD is red. !!!\n" +
          `Healing attempt ${attempt}/${maxAttempts}: a healer agent runs on ` +
          "the host (merge-to-head worktree) and the gate re-runs afterwards.",
        );
        console.error(`Gate log: ${logPath}`);
        for (const block of failures) {
          console.error(`  ${block.split("\n")[0]}`);
        }
      },
      runHealer: async ({ attempt, maxAttempts, logPath, failures }) => {
        // -------------------------------------------------------------------
        // Phase 4: Heal — on the HOST, not in a Docker sandbox: the failing
        // suites are container-backed and the sandboxes have no Docker, so a
        // sandboxed healer could never build the red loop that diagnosing a
        // defect demands. merge-to-head still isolates it in a fresh worktree
        // of tracked files (no .env), on a temp branch that merges back only
        // its commits. bypassPermissions replaces the
        // --dangerously-skip-permissions the Docker runs get: an unattended
        // `claude -p` on the host otherwise denies every edit and command.
        // That trust decision is exactly why healing is OFF by default.
        // -------------------------------------------------------------------
        await sandcastle.run({
          sandbox: noSandbox({ env: { ...config.sandbox.hostEnv } }),
          branchStrategy: { type: "merge-to-head" },
          name: `healer-${attempt}`,
          // 3, like the reviewer: room to commit after a long foreground
          // suite, then finish and report.
          maxIterations: 3,
          idleTimeoutSeconds: limits.idleTimeoutSeconds,
          agent: agentFor(models.healer, { permissionMode: "bypassPermissions" }),
          promptFile: config.prompts.heal,
          promptArgs: {
            ATTEMPT: String(attempt),
            MAX_ATTEMPTS: String(maxAttempts),
            GATE_NAME: gateName,
            GATE_COMMAND: gateCommandLine,
            GATE_COMMANDS,
            GATE_LOG: logPath,
            FAILURES: failures.join("\n\n"),
            ISSUES: completedIssues.map((i) => `- ${i.id}: ${i.title}`).join("\n"),
          },
        });
        // Same fail-closed guard as after the merger: an uncommitted healer
        // worktree means HEAD differs from what the healer validated.
        const healerDirt = preserveMergerDirt(process.cwd());
        if (healerDirt.reports.length > 0 || healerDirt.scanErrors.length > 0) {
          if (healerDirt.reports.length > 0) {
            writeStderrSync(mergerDirtErrorBlock(healerDirt.reports) + "\n");
          }
          if (healerDirt.scanErrors.length > 0) {
            writeStderrSync(mergerDirtScanErrorBlock(healerDirt.scanErrors) + "\n");
          }
          process.exit(1);
        }
      },
    });

    for (const warning of outcome.warnings) {
      // Cleanup could not confirm an empty gate process group — survivors
      // may still hold containers; never swallow that.
      console.warn(`\nWARNING: ${warning}`);
    }

    if (outcome.status === "interrupted") {
      // The user aborted (Ctrl+C): stop the loop, no misleading "fix and
      // rerun" advice — HEAD is simply unverified because the run was cut.
      console.error(
        "\nPost-merge gate interrupted — HEAD is UNVERIFIED. " +
        "Rerun Sandcastle (or the gate) to verify the merged HEAD.",
      );
      process.exit(130);
    }

    if (outcome.status !== "green") {
      const last = outcome.last;
      if (outcome.status === "unverified") {
        console.error(
          "\n!!! POST-MERGE GATE COULD NOT VERIFY HEAD. !!!\n" +
          `Gate: ${last.failed}\n` +
          `Reason: ${last.unverified}\n` +
          "The merge itself landed — this is an environment failure, " +
          "NOT evidence that the code is broken. Not handed to the healer.",
        );
      } else if (outcome.status === "healer-failed") {
        console.error(
          `\n!!! HEALER ATTEMPT ${outcome.attempt} FAILED TO RUN: ${outcome.error} !!!\n` +
          "The merged HEAD is still red (the last gate verdict stands). " +
          "Healer commits that did not merge back are preserved on the temp " +
          "branch named above, if one was created. Land the fix before " +
          "rerunning Sandcastle.",
        );
      } else {
        console.error(
          "\n!!! POST-MERGE GATE FAILED: the merged HEAD is red. !!!\n" +
          `Failed gate: ${last.failed}\n` +
          (outcome.attempts === 0
            ? "Healing is disabled (SANDCASTLE_HEAL_ATTEMPTS=0). "
            : `Still red after ${outcome.attempts} healing attempt(s); ` +
            "the healer's commits (if any) are on HEAD. ") +
          "The next planning round must NOT run on a red HEAD. " +
          "Land the fix before rerunning Sandcastle.",
        );
      }
      if (last.logPath !== undefined) {
        console.error(`\nFull gate log: ${last.logPath}`);
        for (const line of readGateExcerpt(last.logPath, last.parser)) {
          console.error(line);
        }
      }
      process.exit(1);
    }

    console.log(
      outcome.healedAfter === 0
        ? "Post-merge gate passed; the merged HEAD is green."
        : `Post-merge gate passed after ${outcome.healedAfter} healing ` +
        "attempt(s); the healer's fix is on HEAD and the merged HEAD is green.",
    );
  }
}

console.log("\nAll done.");
