// Usage-limit wait — ride out a spent Claude subscription instead of ending
// the run.
//
// When an account's usage runs out, `claude -p` exits non-zero and the library
// reports a plain agent error, exactly like any other failure. Left alone, a
// planner failure crashes the process, a merger or healer failure ends the run
// and every queued implementer fails in turn, so an unattended run stops at the
// first limit.
//
// Every agent phase therefore runs through `run(label, model, fn)` below:
//
//   - A failed phase is diagnosed by PROBING its model, never by matching the
//     error text. The wording moves between CLI releases, and the library
//     builds its error from stderr first, where unrelated warnings land, so the
//     limit text is often not in the error at all. If the model answers, the
//     failure was real and the ORIGINAL error is re-thrown unchanged. If it
//     does not, the account cannot reach the model (a usage limit, or an
//     outage), so the phase waits and then re-runs.
//   - One wait per model, shared by every phase waiting on it: three
//     implementers that hit the limit together cost one probe per poll, not
//     three.
//   - A budget per call, starting at the FIRST limit hit. An implementer that
//     worked for hours before the limit still gets the whole wait; a phase that
//     keeps failing cannot loop forever, because its retries share the budget.
//     When the budget runs out the original error is re-thrown (a weekly limit,
//     a dead token). A budget of 0 turns the wait off and spends no probe.
//
// The startup self-check (`checkUsageProbe`) keeps the wait fail-safe: when the
// host CLI cannot run the probe at all, every probe would fail and every REAL
// phase failure would turn into a long wait, so the wait is switched off for
// the run. A probe refused because the quota is already spent is the one
// exception: the CLI works, so the wait stays on and the first phase waits for
// the reset.
//
// Pure by injection — probe, sleep, clock and log all arrive as arguments — so
// the tests below drive every path with no CLI, no model and no real time.

/**
 * What one probe of a model established.
 *
 * - `answered`: the model replied, so the account can reach it.
 * - `limited`: the CLI ran and was refused with an error that names a limit.
 * - `unavailable`: anything else — no CLI, a flag it does not know, bad
 *   credentials, a timeout, output that cannot be parsed.
 *
 * Only the startup self-check tells `limited` from `unavailable`. Mid-run, both
 * mean "not answering" and the phase waits: the mid-run decision trusts the
 * probe alone.
 */
export type ProbeOutcome =
  | { readonly verdict: "answered" }
  | { readonly verdict: "limited" | "unavailable"; readonly detail: string };

/** Probes one model. Must not throw; a throw is treated as `unavailable`. */
export type Probe = (model: string) => Promise<ProbeOutcome>;

/** What the probe asks. A one-word reply keeps it as cheap as a call can be. */
export const PROBE_PROMPT = "Reply with the single word OK.";

/**
 * The `claude` arguments for one probe of `model`: print mode, JSON output,
 * one turn, no tools, no MCP servers and no saved session. The caller runs it
 * from an empty temporary directory, so no repository instructions load
 * either, and closes stdin so print mode never waits for more prompt.
 */
export function probeArgs(model: string): string[] {
  return [
    "-p",
    PROBE_PROMPT,
    "--model",
    model,
    "--output-format",
    "json",
    "--max-turns",
    "1",
    "--tools",
    "",
    "--strict-mcp-config",
    "--no-session-persistence",
  ];
}

function asRecord(value: unknown): Record<string, unknown> | undefined {
  return typeof value === "object" && value !== null && !Array.isArray(value)
    ? (value as Record<string, unknown>)
    : undefined;
}

/**
 * The `result` event in whatever the CLI printed. Newer releases print
 * `--output-format json` as an array of stream events, older ones as a bare
 * result object; both are accepted. The LAST result event wins.
 */
function resultEvent(parsed: unknown): Record<string, unknown> | undefined {
  const events = Array.isArray(parsed) ? parsed : [parsed];
  let found: Record<string, unknown> | undefined;
  for (const event of events) {
    const record = asRecord(event);
    if (record?.type === "result") {
      found = record;
    }
  }
  return found;
}

/** A refusal that names a limit. Consulted by the startup decision only. */
const LIMIT_TEXT = /\blimits?\b/i;
/** The HTTP status the API answers a spent quota with. */
const TOO_MANY_REQUESTS = 429;

function clip(text: string): string {
  const flat = text.replace(/\s+/g, " ").trim();
  return flat.length > 200 ? `${flat.slice(0, 200)}…` : flat;
}

/**
 * Classify one probe from the CLI's stdout (and, when the CLI could not be run
 * or exited non-zero, what the process reported).
 *
 * Only a `result` event with `is_error: false` counts as answered. An error
 * result counts as `limited` when it carries the API's 429 status or its text
 * names a limit; everything else, unparseable output included, is
 * `unavailable`.
 */
export function classifyProbeOutput(stdout: string, failure?: string): ProbeOutcome {
  let parsed: unknown;
  try {
    parsed = JSON.parse(stdout);
  } catch {
    return {
      verdict: "unavailable",
      detail: clip(failure ?? (stdout.trim() === "" ? "no output" : stdout)),
    };
  }

  const result = resultEvent(parsed);
  if (result === undefined) {
    return {
      verdict: "unavailable",
      detail: clip(failure ?? "the output held no result event"),
    };
  }
  if (result.is_error === false) {
    return { verdict: "answered" };
  }

  const text = typeof result.result === "string" ? result.result : "";
  const limited =
    result.api_error_status === TOO_MANY_REQUESTS || LIMIT_TEXT.test(text);
  return {
    verdict: limited ? "limited" : "unavailable",
    detail: clip(text !== "" ? text : (failure ?? "an error result with no text")),
  };
}

export interface UsageWaitOptions {
  readonly probe: Probe;
  readonly sleep: (ms: number) => Promise<void>;
  readonly now: () => number;
  readonly log: (line: string) => void;
  /** Time between probes while a model is not answering. */
  readonly pollMs: number;
  /** The per-call wait budget, counted from the first limit hit. 0 = off. */
  readonly maxWaitMs: number;
}

export interface UsageWait {
  /** False when the budget is 0: `run` then only ever calls `fn` once. */
  readonly enabled: boolean;
  /**
   * Run one agent phase. When it throws and `model` is not answering, wait
   * until it answers again (up to the budget) and run the phase again.
   */
  run<T>(label: string, model: string, fn: () => Promise<T>): Promise<T>;
}

function minutes(ms: number): string {
  const total = Math.round(ms / 60_000);
  return total < 120 || total % 60 !== 0 ? `${total} min` : `${total / 60} h`;
}

export function createUsageWait(options: UsageWaitOptions): UsageWait {
  const { probe, sleep, now, log, pollMs, maxWaitMs } = options;

  // The ONE in-flight probe per model. A phase that needs an answer while a
  // probe of its model is already on its way joins it instead of starting its
  // own, which is what keeps concurrent waiters on a single probe loop.
  const inFlight = new Map<string, Promise<boolean>>();

  const answers = (model: string, afterMs: number): Promise<boolean> => {
    const pending = inFlight.get(model);
    if (pending !== undefined) {
      return pending;
    }
    const next = (async () => {
      try {
        if (afterMs > 0) {
          await sleep(afterMs);
        }
        return (await probe(model)).verdict === "answered";
      } catch {
        return false;
      }
    })();
    inFlight.set(model, next);
    // Registered before any joiner's await, so the slot is free again by the
    // time a joiner asks for the next probe. Never rejects: see the catch.
    void next.then(() => {
      if (inFlight.get(model) === next) {
        inFlight.delete(model);
      }
    });
    return next;
  };

  /** Poll until `model` answers (true) or `deadline` passes (false). */
  const answersBy = async (model: string, deadline: number): Promise<boolean> => {
    while (now() < deadline) {
      if (await answers(model, pollMs)) {
        return true;
      }
    }
    return false;
  };

  return {
    enabled: maxWaitMs > 0,
    async run<T>(label: string, model: string, fn: () => Promise<T>): Promise<T> {
      let firstHit: number | undefined;
      for (;;) {
        try {
          return await fn();
        } catch (error) {
          if (maxWaitMs <= 0 || (await answers(model, 0))) {
            throw error;
          }
          firstHit ??= now();
          const deadline = firstHit + maxWaitMs;
          const left = deadline - now();
          if (left <= 0) {
            log(
              `  ${label}: ${model} is still not answering and the ` +
                `${minutes(maxWaitMs)} usage-limit wait is spent. Giving up.`,
            );
            throw error;
          }
          log(
            `  ${label}: failed and ${model} is not answering — most likely a ` +
              `usage limit. Probing every ${minutes(pollMs)} for up to ` +
              `${minutes(left)}, then re-running the phase.`,
          );
          if (!(await answersBy(model, deadline))) {
            log(
              `  ${label}: ${model} did not answer within the ` +
                `${minutes(maxWaitMs)} usage-limit wait. Giving up with the ` +
                "phase's original error.",
            );
            throw error;
          }
          log(`  ${label}: ${model} answers again — re-running the phase.`);
        }
      }
    },
  };
}

/**
 * The startup self-check: probe once, for real, and decide whether the wait
 * can be trusted for this run. Returns whether it stays on.
 *
 * Fails safe. A probe that does not answer for any reason other than a spent
 * quota means the host CLI cannot probe (missing, too old for one of the
 * flags, not logged in), and a wait built on a broken probe would turn every
 * real phase failure into a long wait — so it is switched off, loudly, and the
 * run behaves as it would without it. A probe refused for a limit means the
 * CLI works and the quota is simply spent: the natural moment to relaunch a
 * stopped run. The wait stays on, and the first phase waits for the reset.
 */
export async function checkUsageProbe(options: {
  readonly probe: Probe;
  readonly model: string;
  readonly maxWaitMs: number;
  readonly log: (line: string) => void;
}): Promise<boolean> {
  const { probe, model, maxWaitMs, log } = options;
  if (maxWaitMs <= 0) {
    log("Usage-limit wait: OFF (the wait budget is 0).");
    return false;
  }

  let outcome: ProbeOutcome;
  try {
    outcome = await probe(model);
  } catch (error) {
    outcome = {
      verdict: "unavailable",
      detail: error instanceof Error ? error.message : String(error),
    };
  }

  switch (outcome.verdict) {
    case "answered":
      log(`Usage-limit wait: ON (the startup probe of ${model} answered).`);
      return true;
    case "limited":
      log(
        `Usage-limit wait: ON. The startup probe of ${model} was refused for a ` +
          `limit (${outcome.detail}), so the quota is spent right now; the ` +
          "first phase will wait for the reset.",
      );
      return true;
    case "unavailable":
      log(
        `Usage-limit wait: OFF for this run. The host \`claude\` CLI could not ` +
          `answer the startup probe of ${model} (${outcome.detail}), so a ` +
          "phase failure could not be told apart from a usage limit. A failed " +
          "phase ends the run as it would without the wait.",
      );
      return false;
  }
}
