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

import { MAIN_ACCOUNT, type ClaudeAccount } from "./accounts.mts";

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

/**
 * Probes one model on one account. Must not throw; a throw is treated as
 * `unavailable`.
 */
export type Probe = (model: string, account: ClaudeAccount) => Promise<ProbeOutcome>;

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
  /**
   * The accounts a phase may run on, in priority order: the main account
   * first, then the optional fallback. Defaults to the main account alone,
   * which is the plain wait with no switching.
   */
  readonly accounts?: readonly ClaudeAccount[];
}

export interface UsageWait {
  /** False when the budget is 0: `run` then only ever calls `fn` once. */
  readonly enabled: boolean;
  /**
   * Run one agent phase on the account the run is on. When it throws and
   * `model` is not answering on that account, re-run it at once on the first
   * other account that answers; when none does, wait until one answers (up to
   * the budget) and run the phase again there. `fn` is handed the account it
   * runs on.
   */
  run<T>(
    label: string,
    model: string,
    fn: (account: ClaudeAccount) => Promise<T>,
  ): Promise<T>;
}

function minutes(ms: number): string {
  const total = Math.round(ms / 60_000);
  return total < 120 || total % 60 !== 0 ? `${total} min` : `${total / 60} h`;
}

export function createUsageWait(options: UsageWaitOptions): UsageWait {
  const { probe, sleep, now, log, pollMs, maxWaitMs } = options;
  const accounts = options.accounts ?? [MAIN_ACCOUNT];
  if (accounts.length === 0) {
    throw new Error("The usage-limit wait needs at least one account");
  }
  const main = accounts[0]!;
  const multi = accounts.length > 1;
  const on = (account: ClaudeAccount) => (multi ? ` on the ${account.name} account` : "");

  // The account new phases start on. Moves to another account when the one
  // the run is on stops answering, and back to the main account once it
  // answers again.
  let active = main;
  // When the main account was last asked whether it answers again, so that is
  // asked at most once per poll interval however many phases start.
  let mainCheckedAt = 0;

  const switchTo = (account: ClaudeAccount, reason: string): void => {
    if (account === active) {
      return;
    }
    active = account;
    if (account !== main) {
      mainCheckedAt = now();
    }
    log(`  Usage-limit wait: ${reason} New phases start on the ${account.name} account.`);
  };

  // The ONE in-flight probe per account and model. A phase that needs an
  // answer while a probe of the same pair is already on its way joins it
  // instead of starting its own, which is what keeps concurrent waiters on a
  // single probe loop.
  const inFlight = new Map<string, Promise<boolean>>();

  const answers = (
    model: string,
    account: ClaudeAccount,
    afterMs: number,
  ): Promise<boolean> => {
    const key = `${account.name}\u0000${model}`;
    const pending = inFlight.get(key);
    if (pending !== undefined) {
      return pending;
    }
    const next = (async () => {
      try {
        if (afterMs > 0) {
          await sleep(afterMs);
        }
        return (await probe(model, account)).verdict === "answered";
      } catch {
        return false;
      }
    })();
    inFlight.set(key, next);
    // Registered before any joiner's await, so the slot is free again by the
    // time a joiner asks for the next probe. Never rejects: see the catch.
    void next.then(() => {
      if (inFlight.get(key) === next) {
        inFlight.delete(key);
      }
    });
    return next;
  };

  /**
   * One poll: wait a poll interval, then ask every account in priority order.
   * The first that answers wins, so the main account wins a tie.
   */
  const pollOnce = async (model: string): Promise<ClaudeAccount | undefined> => {
    if (await answers(model, main, pollMs)) {
      return main;
    }
    for (const account of accounts.slice(1)) {
      if (await answers(model, account, 0)) {
        return account;
      }
    }
    return undefined;
  };

  /** Poll until an account answers `model`, or `deadline` passes. */
  const answeringBy = async (
    model: string,
    deadline: number,
  ): Promise<ClaudeAccount | undefined> => {
    while (now() < deadline) {
      const account = await pollOnce(model);
      if (account !== undefined) {
        return account;
      }
    }
    return undefined;
  };

  /**
   * The account a new phase starts on. While the run is on another account,
   * first ask whether the main account answers again — at most once per poll
   * interval, shared by every phase. Phases already running stay where they
   * are.
   */
  const accountFor = async (model: string): Promise<ClaudeAccount> => {
    if (active === main || now() - mainCheckedAt < pollMs) {
      return active;
    }
    mainCheckedAt = now();
    if (await answers(model, main, 0)) {
      switchTo(main, `the main account answers ${model} again.`);
    }
    return active;
  };

  return {
    enabled: maxWaitMs > 0,
    async run<T>(
      label: string,
      model: string,
      fn: (account: ClaudeAccount) => Promise<T>,
    ): Promise<T> {
      if (maxWaitMs <= 0) {
        return fn(main);
      }
      let account = await accountFor(model);
      let firstHit: number | undefined;
      for (;;) {
        try {
          return await fn(account);
        } catch (error) {
          // The account the phase RAN on is the one diagnosed: a real failure
          // on the fallback is re-thrown, never bounced back to the main one.
          if (await answers(model, account, 0)) {
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

          // Another account that answers takes the phase at once.
          let next: ClaudeAccount | undefined;
          for (const other of accounts) {
            if (other !== account && (await answers(model, other, 0))) {
              next = other;
              break;
            }
          }
          if (next !== undefined) {
            log(
              `  ${label}: failed and ${model} is not answering on the ` +
                `${account.name} account — re-running the phase at once on ` +
                `the ${next.name} account.`,
            );
            switchTo(next, `the ${account.name} account is not answering ${model}.`);
            account = next;
            continue;
          }

          log(
            `  ${label}: failed and ${model} is not answering${multi ? " on any account" : ""} — ` +
              `most likely a usage limit. Probing every ${minutes(pollMs)} for up to ` +
              `${minutes(left)}, then re-running the phase.`,
          );
          const back = await answeringBy(model, deadline);
          if (back === undefined) {
            log(
              `  ${label}: ${model} did not answer within the ` +
                `${minutes(maxWaitMs)} usage-limit wait. Giving up with the ` +
                "phase's original error.",
            );
            throw error;
          }
          log(`  ${label}: ${model} answers again${on(back)} — re-running the phase.`);
          switchTo(back, `the ${back.name} account answers ${model}.`);
          account = back;
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
    outcome = await probe(model, MAIN_ACCOUNT);
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

/**
 * The fallback account's startup check: probe it once, so a broken token is
 * reported now rather than discovered mid-round, on the night the main
 * account runs out. Returns whether the fallback stays in the run.
 *
 * A fallback that answers, or is merely limited right now, stays: a spent
 * quota recovers on its own. One the CLI cannot use at all (a typo in the
 * token, a revoked one) is dropped with a warning, and the run goes on with
 * the main account alone.
 */
export async function checkFallbackAccount(options: {
  readonly probe: Probe;
  readonly model: string;
  readonly account: ClaudeAccount;
  readonly log: (line: string) => void;
}): Promise<boolean> {
  const { probe, model, account, log } = options;
  let outcome: ProbeOutcome;
  try {
    outcome = await probe(model, account);
  } catch (error) {
    outcome = {
      verdict: "unavailable",
      detail: error instanceof Error ? error.message : String(error),
    };
  }

  switch (outcome.verdict) {
    case "answered":
      log(`Fallback account: ON (the startup probe of ${model} on it answered).`);
      return true;
    case "limited":
      log(
        `Fallback account: ON. Its startup probe of ${model} was refused for a ` +
          `limit (${outcome.detail}); it is used once that clears.`,
      );
      return true;
    case "unavailable":
      log(
        `Fallback account: DROPPED for this run. Its startup probe of ${model} ` +
          `could not be answered (${outcome.detail}), so its token looks ` +
          `broken. Check ${account.tokenVar} in .sandcastle/.env. The run goes ` +
          "on with the main account alone.",
      );
      return false;
  }
}
