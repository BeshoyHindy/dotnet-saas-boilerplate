import type { PostMergeGateResult } from "./post-merge-gate.mts";

// ---------------------------------------------------------------------------
// Post-merge gate healing — the pure loop main.mts drives (Phase 4).
//
// A red post-merge gate ends the run by default: "Land the fix before rerunning
// Sandcastle." But most reds are one-file contract defects that a focused
// debugging session fixes in minutes, while each one costs a human restart and
// the rest of the night's iterations. This module bounds the same repair: run
// the gate, and on a DELIVERED red verdict hand the failures to a healer agent
// on the host (Docker lives there; sandboxes have none), then run the gate
// again, up to `maxAttempts` times.
//
// What it never heals: an UNVERIFIED gate (Docker died, spawn error, timeout)
// or a user interrupt — there is no defect to fix, only an environment to
// restore, and a healer chasing a phantom would "fix" sound code.
//
// Healing is opt-in (`limits.healAttempts`, default 0) because the healer runs
// on the host with permission prompts bypassed — a real trust decision that
// belongs to the operator, not to this loop.
//
// Pure by injection so the unit tests drive every branch with fakes.
// ---------------------------------------------------------------------------

export type RedGateResult = Extract<PostMergeGateResult, { ok: false }>;

export type HealAttemptInput = {
  /** 1-based attempt number. */
  readonly attempt: number;
  readonly maxAttempts: number;
  readonly gateName: string;
  /** The gate's log path — the healer reads the full log from it. */
  readonly logPath: string;
  /** The concise failure blocks the healer is briefed with. */
  readonly failures: readonly string[];
};

/** Runs the healer once; a throw means the healer itself broke (merge-back failed, agent crashed). */
export type HealRunner = (input: HealAttemptInput) => Promise<void>;

/** Runs the gate once; `logSuffix` tags the log file of a re-run (`-heal-1`). */
export type GateRunner = (logSuffix: string) => Promise<PostMergeGateResult>;

export type HealOutcome =
  | {
      readonly status: "green";
      /** How many healer runs it took — 0 when the first gate run was green. */
      readonly healedAfter: number;
      readonly warnings: readonly string[];
    }
  | {
      /** Still red after every allowed attempt (or healing is disabled). */
      readonly status: "red";
      readonly attempts: number;
      readonly last: RedGateResult;
      readonly warnings: readonly string[];
    }
  | {
      /** The gate could not deliver a verdict — nothing to heal. */
      readonly status: "unverified";
      readonly last: RedGateResult;
      readonly warnings: readonly string[];
    }
  | {
      readonly status: "interrupted";
      readonly last: RedGateResult;
      readonly warnings: readonly string[];
    }
  | {
      /** The healer run itself threw; HEAD is whatever the last gate saw (red). */
      readonly status: "healer-failed";
      readonly attempt: number;
      readonly error: unknown;
      readonly last: RedGateResult;
      readonly warnings: readonly string[];
    };

export type HealOptions = {
  readonly runGate: GateRunner;
  readonly runHealer: HealRunner;
  /** 0 disables healing: the first red verdict is final. */
  readonly maxAttempts: number;
  readonly gateName: string;
  /**
   * Reads the failure blocks out of a red verdict; never throws.
   *
   * Takes the whole verdict rather than just a log path so the caller can read
   * the log with the FAILED GATE's own parser — a build gate and a test gate
   * produce very different logs, and briefing a healer with the wrong shape is
   * worse than briefing it with nothing.
   */
  readonly failuresOf: (red: RedGateResult) => readonly string[];
  readonly onAttempt?: (input: HealAttemptInput) => void;
};

/** The log-file suffix for the gate re-run after healer attempt `n`. */
export function healLogSuffix(attempt: number): string {
  return `-heal-${attempt}`;
}

export async function runGateWithHealing(options: HealOptions): Promise<HealOutcome> {
  const { runGate, runHealer, maxAttempts, gateName, failuresOf, onAttempt } = options;
  if (!Number.isInteger(maxAttempts) || maxAttempts < 0) {
    throw new Error(`maxAttempts must be a non-negative integer, got ${maxAttempts}`);
  }

  const warnings: string[] = [];
  const collect = (result: PostMergeGateResult): void => {
    if (result.warning !== undefined) {
      warnings.push(result.warning);
    }
  };

  let result = await runGate("");
  collect(result);
  let attempt = 0;

  for (;;) {
    if (result.ok) {
      return { status: "green", healedAfter: attempt, warnings };
    }
    if (result.interrupted) {
      return { status: "interrupted", last: result, warnings };
    }
    if (result.unverified !== undefined) {
      return { status: "unverified", last: result, warnings };
    }
    // A delivered red verdict.
    if (attempt >= maxAttempts) {
      return { status: "red", attempts: attempt, last: result, warnings };
    }
    attempt += 1;
    const input: HealAttemptInput = {
      attempt,
      maxAttempts,
      gateName,
      logPath: result.logPath ?? "",
      failures: result.logPath === undefined ? [] : failuresOf(result),
    };
    onAttempt?.(input);
    try {
      await runHealer(input);
    } catch (error) {
      return { status: "healer-failed", attempt, error, last: result, warnings };
    }
    result = await runGate(healLogSuffix(attempt));
    collect(result);
  }
}
