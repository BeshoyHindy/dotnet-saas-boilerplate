// The post-merge gate — running the repo's own gates against the merged HEAD.
//
// Per-branch gates do not compose: individually green branches can still
// produce a red merged HEAD, so the merged whole is verified from the host
// before another planning round can compound merge-seam failures.
//
// WHICH gates run is not decided here. The ordered list arrives as an argument
// (`config.gates`), so this module works for any stack; everything below is
// about running a child process honestly and classifying what came back.
//
// The classification is the point, and it is fail-closed in one direction and
// fail-honest in the other:
//   - RED means the gate DELIVERED a non-zero verdict. Only that may be
//     reported as "the merged HEAD is broken".
//   - UNVERIFIED means the gate never delivered one: Docker died, the binary
//     was missing, the timeout killed it, a signal killed it. That is an
//     environment failure, never evidence about the code, and it is never
//     handed to a healer.

import { execFileSync, spawn } from "node:child_process";
import {
  closeSync,
  mkdirSync,
  openSync,
  readFileSync,
  writeSync,
} from "node:fs";
import { join } from "node:path";

import { resolveLogParser, type LogParserName } from "./log-parsers.mts";

const SANDBOX_DIR = new URL(".", import.meta.url).pathname;
const LOGS_DIR = join(SANDBOX_DIR, "logs");

export type PostMergeGate = {
  name: string;
  file: string;
  args: readonly string[];
  timeoutMs: number;
  /** Which parser turns this gate's log into an excerpt / failure blocks. */
  parser?: LogParserName;
  /**
   * The gate cannot run without a reachable Docker daemon, so a failure is
   * only evidence about HEAD if Docker is still up when it fails.
   */
  requiresDocker?: boolean;
};

export type GateExecResult =
  | {
      readonly ok: true;
      readonly logPath: string;
      /** Cleanup could not confirm an empty process group — surface it. */
      readonly warning?: string;
    }
  | {
      readonly ok: false;
      readonly logPath: string;
      readonly error?: Error;
      /** The run was aborted by a user signal (SIGINT/SIGTERM) — stop, don't retry. */
      readonly interrupted?: boolean;
      /** Cleanup could not confirm an empty process group — surface it. */
      readonly warning?: string;
    };

export type PostMergeGateExec = (
  file: string,
  args: readonly string[],
  timeoutMs: number,
  logPath: string,
) => GateExecResult | Promise<GateExecResult>;

/** True when the Docker daemon answers. */
export type DockerProbe = () => boolean;

export type PostMergeGateResult =
  | { ok: true; warning?: string }
  | {
      ok: false;
      failed: string;
      /** The failed gate's parser, so the caller can read its log the right way. */
      parser?: LogParserName;
      /**
       * Set when the gate failed because its environment was gone rather than
       * because the code is bad — HEAD is UNVERIFIED, not proven red.
       */
      unverified?: string;
      /** Path to the captured gate log, when one was created. */
      logPath?: string;
      /** The spawn or timeout error, when the failure was environmental. */
      error?: Error;
      /** The user aborted the run (Ctrl+C) — the caller should stop entirely. */
      interrupted?: boolean;
      /** Cleanup could not confirm an empty process group — print it. */
      warning?: string;
    };

function padTwo(n: number): string {
  return String(n).padStart(2, "0");
}

/** Build the log-file path for a gate run. Exposed so main.mts can print the
 *  `tail -f` hint before the gate starts.
 */
export function makePostMergeGateLogPath(timestamp = new Date(), suffix = ""): string {
  const stamp =
    `${timestamp.getFullYear()}` +
    `${padTwo(timestamp.getMonth() + 1)}` +
    `${padTwo(timestamp.getDate())}-` +
    `${padTwo(timestamp.getHours())}` +
    `${padTwo(timestamp.getMinutes())}` +
    `${padTwo(timestamp.getSeconds())}`;
  return join(LOGS_DIR, `${stamp}${suffix}-post-merge-gate.log`);
}

/** How long a SIGTERM'd gate gets to die before its whole tree is SIGKILL'd. */
const KILL_GRACE_MS = 10_000;

/**
 * Best-effort synchronous stderr write: exit() cannot truncate it, and it
 * must NEVER throw (EPIPE/EAGAIN) — a failed log line aborting the reap or
 * the exit path would be worse than the lost line itself.
 */
export function writeStderrSync(message: string): void {
  try {
    writeSync(2, message);
  } catch {
    // Nothing sane to do — never let logging break control flow.
  }
}

/** The one survivor-warning wording, shared by the settle and abort paths. */
export function survivorWarning(pid: number | undefined): string {
  return (
    "the gate's process group still had survivors after " +
    `${KILL_GRACE_MS} ms of SIGKILL — inspect (and kill) them manually: ` +
    `pgrep -g ${pid ?? "?"}`
  );
}

/**
 * Async on purpose: the gate runs detached in its OWN process group so that a
 * timeout (or a user Ctrl+C) can kill the whole tree — a test runner spawns
 * test-host children that a plain parent-only SIGTERM orphans, still holding
 * their containers. Detaching removes the terminal's own SIGINT delivery to the
 * child, so the parent forwards SIGINT/SIGTERM to the group and reports the
 * run as user-interrupted (a sync spawnSync version could do neither: it
 * blocked the event loop, deferring every signal handler until the gate ended).
 */
const execPostMergeGate: PostMergeGateExec = (file, args, timeoutMs, logPath) =>
  new Promise<GateExecResult>((resolve) => {
    let fd: number;
    try {
      mkdirSync(LOGS_DIR, { recursive: true });
      fd = openSync(logPath, "w");
    } catch (err) {
      resolve({
        ok: false,
        logPath,
        error: err instanceof Error ? err : new Error(String(err)),
      });
      return;
    }

    const child = spawn(file, [...args], {
      stdio: ["ignore", fd, fd],
      detached: true,
    });

    let settled = false;
    let timedOut = false;
    let interruptedBy: NodeJS.Signals | undefined;

    const killTree = (signal: NodeJS.Signals): void => {
      if (child.pid === undefined) {
        return;
      }
      try {
        process.kill(-child.pid, signal); // the whole group, not just the runner
      } catch {
        try {
          child.kill(signal);
        } catch {
          // Already gone.
        }
      }
    };

    const escalate = (): void => {
      killTree("SIGTERM");
      setTimeout(() => killTree("SIGKILL"), KILL_GRACE_MS).unref();
    };

    /** True once no member of the gate's process group remains. */
    const groupGone = (): boolean => {
      if (child.pid === undefined) {
        return true;
      }
      try {
        process.kill(-child.pid, 0);
        return false;
      } catch (err) {
        // ONLY ESRCH proves the group is empty — EPERM means members exist
        // that we may not signal, which is the opposite of "gone".
        return (err as NodeJS.ErrnoException).code === "ESRCH";
      }
    };

    /**
     * SIGKILL the group and WAIT until it is empty (bounded): sending a
     * signal is not reaping, and the parent exits right after this promise
     * resolves — resolution must therefore mean "no descendant remains".
     * The SIGKILL is re-sent each poll so a fork racing the first sweep is
     * still caught. Returns false when the bound expires with survivors
     * (pathological uninterruptible-sleep processes) — the caller must
     * surface that, never swallow it.
     */
    const reapGroup = async (): Promise<boolean> => {
      const deadline = Date.now() + KILL_GRACE_MS;
      killTree("SIGKILL");
      while (!groupGone()) {
        if (Date.now() >= deadline) {
          return false;
        }
        await new Promise((r) => setTimeout(r, 50));
        killTree("SIGKILL");
      }
      return true;
    };

    const timer = setTimeout(() => {
      timedOut = true;
      escalate();
    }, timeoutMs);

    const onSignal = (signal: NodeJS.Signals): void => {
      interruptedBy ??= signal;
      escalate();
    };
    process.on("SIGINT", onSignal);
    process.on("SIGTERM", onSignal);

    const settle = (result: GateExecResult): void => {
      if (settled) {
        return;
      }
      settled = true;
      clearTimeout(timer);
      process.removeListener("SIGINT", onSignal);
      process.removeListener("SIGTERM", onSignal);
      try {
        closeSync(fd);
      } catch {
        // Best effort — the run is over either way.
      }
      resolve(result);
    };

    const onSpawnError = (err: Error): void =>
      settle({ ok: false, logPath, error: err });
    child.once("error", onSpawnError);
    child.once("close", (status, signal) => void (async () => {
      // The verdict is FROZEN the moment the child exits: stop the timeout
      // timer and the signal forwarding NOW and snapshot their state — the
      // reap below awaits, and a timer firing (or a Ctrl+C landing) during
      // that wait must not reclassify a delivered verdict as timeout or
      // interrupt (a green run finishing at 44:59 of a 45:00 budget stays
      // green).
      clearTimeout(timer);
      process.removeListener("SIGINT", onSignal);
      process.removeListener("SIGTERM", onSignal);
      const wasInterrupted = interruptedBy;
      const wasTimedOut = timedOut;

      // A LATE child "error" event (a failed signal delivery during the reap)
      // must neither overwrite the frozen verdict via settle() nor crash the
      // process as an unhandled 'error'.
      child.removeListener("error", onSpawnError);
      child.on("error", () => {});

      // Ctrl+C during the reap window: Node's default handler would kill the
      // orchestrator INSTANTLY, leaking the very survivors being reaped and
      // printing nothing. Instead, mark the run for abort and let the BOUNDED
      // confirmation loop below finish (≤ the grace window) — cleanup is
      // never cut short, the survivor warning still prints, and repeated
      // Ctrl+C is swallowed until the sweep is done.
      let abortAfterReap = false;
      const onReapSignal = (): void => {
        abortAfterReap = true;
        // Synchronous: console.error queues an async write on pipes, and the
        // process may exit right after the reap — the notice must not vanish.
        writeStderrSync(
          "\nInterrupt received — finishing the gate process-group cleanup " +
            "(bounded) before exiting.\n",
        );
      };
      process.on("SIGINT", onReapSignal);
      process.on("SIGTERM", onReapSignal);

      // GUARANTEED tree cleanup on EVERY exit: "close" only proves the DIRECT
      // child died. Crashed test hosts can outlive a red (or even green) exit
      // still holding their containers, the grace SIGKILL timer is unref'd, and
      // main.mts exits right after this promise resolves — so the group is
      // reaped and confirmed empty before settling, whatever the verdict.
      // Cost on clean runs: build-server daemons started by the gate die too —
      // a few seconds of warm-up next build, never a leak.
      const reaped = await reapGroup();
      process.removeListener("SIGINT", onReapSignal);
      process.removeListener("SIGTERM", onReapSignal);
      if (abortAfterReap) {
        if (!reaped) {
          // Synchronous, not console.error: process.exit() below would drop
          // a still-queued async stderr write and swallow the warning.
          writeStderrSync(`WARNING: ${survivorWarning(child.pid)}\n`);
        }
        writeStderrSync("Gate run aborted by the user during cleanup.\n");
        process.exit(130);
      }
      const warning = reaped ? undefined : survivorWarning(child.pid);

      if (wasInterrupted !== undefined) {
        settle({
          ok: false,
          logPath,
          interrupted: true,
          error: new Error(`the gate run was interrupted by ${wasInterrupted}`),
          warning,
        });
      } else if (wasTimedOut) {
        settle({
          ok: false,
          logPath,
          error: new Error(
            `the gate timed out after ${timeoutMs} ms (ETIMEDOUT); ` +
              "its process tree was killed",
          ),
          warning,
        });
      } else if (signal !== null) {
        // Killed from outside (OOM SIGKILL etc.) — a death, not a verdict.
        settle({
          ok: false,
          logPath,
          error: new Error(`the gate process was killed by signal ${signal}`),
          warning,
        });
      } else if (status === 0) {
        settle({ ok: true, logPath, warning });
      } else if (status === null) {
        settle({
          ok: false,
          logPath,
          error: new Error("the gate process exited without a status"),
          warning,
        });
      } else {
        // A real verdict: the suite is red.
        settle({ ok: false, logPath, warning });
      }
    })());
  });

const probeDocker: DockerProbe = () => {
  try {
    execFileSync("docker", ["info"], { stdio: "ignore", timeout: 60_000 });
    return true;
  } catch {
    return false;
  }
};

/**
 * True when the gate log shows Testcontainers' resource reaper (ryuk) failing
 * to start — "ResourceReaperException: Initialization has been cancelled".
 * Every test in the affected project then fails in ~1 ms before touching the
 * code under test, which is a Docker-contention symptom (the reaper's start
 * timed out while the daemon was still tearing down the round's sandboxes),
 * not a verdict on HEAD. Observed failing a whole otherwise-sound project's
 * tests, with the same suite passing unchanged minutes later.
 */
export function logShowsReaperStartFailure(logPath: string): boolean {
  try {
    return readFileSync(logPath, "utf8").includes("ResourceReaperException");
  } catch {
    return false;
  }
}

/** Read a gate log and hand it to `parse`; [] when the log cannot be read. */
function readLog(logPath: string, parse: (content: string) => string[]): string[] {
  try {
    return parse(readFileSync(logPath, "utf8"));
  } catch {
    // The log may not exist when the spawn itself failed (e.g. openSync threw
    // before the child ran) — the failure report must never crash over it.
    return [];
  }
}

/** A short console excerpt of why a gate went red, read with its own parser. */
export function readGateExcerpt(
  logPath: string,
  parser?: LogParserName,
): string[] {
  return readLog(logPath, resolveLogParser(parser).excerpt);
}

/** The failure blocks a healer agent is briefed with, read with the gate's parser. */
export function readGateFailures(
  logPath: string,
  parser?: LogParserName,
): string[] {
  return readLog(logPath, resolveLogParser(parser).failures);
}

/**
 * Run the gates in order, stopping at the first failure.
 *
 * A Docker-backed gate is re-classified when it fails and Docker is no longer
 * reachable: Docker Desktop dying mid-run (it hosts every agent sandbox as well
 * as the container-backed test suites) once failed an entire integration suite,
 * and the run then reported "the merged HEAD is red. Land the fix" — sending a
 * human hunting for a defect that did not exist. The daemon is probed AFTER the
 * failure, not before, because it was up when that gate started.
 */
export async function runPostMergeGates(
  gates: readonly PostMergeGate[],
  execImpl: PostMergeGateExec = execPostMergeGate,
  dockerProbe: DockerProbe = probeDocker,
  onGateStart?: (gate: PostMergeGate, logPath: string) => void,
  /** Tags every log file of this invocation (`-heal-1` for a post-healer re-run). */
  logSuffix = "",
): Promise<PostMergeGateResult> {
  const warnings: string[] = [];
  for (const gate of gates) {
    let logPath = makePostMergeGateLogPath(new Date(), logSuffix);
    onGateStart?.(gate, logPath);

    let execResult = await execImpl(
      gate.file,
      gate.args,
      gate.timeoutMs,
      logPath,
    );

    // A red verdict whose log shows the Testcontainers reaper failing to start
    // is re-run ONCE before it is believed: that failure mode is Docker
    // contention, not code. A second red run (or one without the marker) is
    // the verdict. Only a delivered verdict is retried — spawn errors, timeouts
    // and interrupts are classified below, never re-run.
    if (
      !execResult.ok &&
      execResult.error === undefined &&
      !execResult.interrupted &&
      gate.requiresDocker &&
      logShowsReaperStartFailure(logPath)
    ) {
      console.warn(
        `\n[${gate.name}] The container resource reaper failed to start ` +
          "(Docker contention, not a verdict on HEAD) — re-running the gate " +
          `once. First attempt's log: ${logPath}`,
      );
      logPath = makePostMergeGateLogPath(new Date(), `${logSuffix}-retry`);
      onGateStart?.(gate, logPath);
      execResult = await execImpl(gate.file, gate.args, gate.timeoutMs, logPath);
    }

    if (!execResult.ok) {
      if (execResult.interrupted) {
        // The USER stopped the run — not an environment failure to "fix and
        // rerun", and certainly not a red HEAD. The caller should exit now.
        return {
          ok: false,
          failed: gate.name,
          parser: gate.parser,
          interrupted: true,
          unverified:
            "the run was interrupted before the gate delivered a verdict.",
          logPath,
          error: execResult.error,
          warning: execResult.warning,
        };
      }
      if (gate.requiresDocker && !dockerProbe()) {
        return {
          ok: false,
          failed: gate.name,
          parser: gate.parser,
          unverified:
            "the Docker daemon is not reachable, and this gate needs it " +
            "(container-backed integration tests). Docker most likely died " +
            "during the run — start it and rerun the gate before treating " +
            "HEAD as broken.",
          logPath,
          error: execResult.error,
          warning: execResult.warning,
        };
      }
      if (execResult.error !== undefined) {
        // The child never delivered a verdict: spawn failure (ENOENT), an fs
        // error opening the log, or the timeout kill. None of those prove the
        // code red — same reasoning as the Docker re-classification above.
        return {
          ok: false,
          failed: gate.name,
          parser: gate.parser,
          unverified:
            "the gate process did not produce a verdict " +
            `(${execResult.error.message}). This is an environment failure — ` +
            "fix it and rerun the gate before treating HEAD as broken.",
          logPath,
          error: execResult.error,
          warning: execResult.warning,
        };
      }
      return {
        ok: false,
        failed: gate.name,
        parser: gate.parser,
        logPath,
        warning: execResult.warning,
      };
    }
    if (execResult.warning !== undefined) {
      // Green verdict, dirty exit: the caller still has to see the warning.
      warnings.push(`[${gate.name}] ${execResult.warning}`);
    }
  }

  return warnings.length > 0
    ? { ok: true, warning: warnings.join("\n") }
    : { ok: true };
}
