import assert from "node:assert/strict";
import test from "node:test";

import {
  healLogSuffix,
  runGateWithHealing,
  type HealAttemptInput,
} from "./gate-heal.mts";
import type { PostMergeGateResult } from "./post-merge-gate.mts";

const GATE = "full test suite";

const green = (warning?: string): PostMergeGateResult =>
  warning === undefined ? { ok: true } : { ok: true, warning };
const red = (logPath = "/logs/gate.log"): PostMergeGateResult => ({
  ok: false,
  failed: GATE,
  parser: "dotnetTest",
  logPath,
});

/** A gate whose verdicts are scripted, recording the log suffix of each run. */
function scriptedGate(verdicts: PostMergeGateResult[]) {
  const suffixes: string[] = [];
  const runGate = async (suffix: string) => {
    suffixes.push(suffix);
    const next = verdicts.shift();
    if (next === undefined) {
      throw new Error("the gate was run more times than scripted");
    }
    return next;
  };
  return { runGate, suffixes };
}

const noFailures = () => [] as const;

test("a green first run heals nothing and runs no healer", async () => {
  const { runGate, suffixes } = scriptedGate([green()]);
  let healerRuns = 0;
  const outcome = await runGateWithHealing({
    runGate,
    runHealer: async () => {
      healerRuns += 1;
    },
    maxAttempts: 2,
    gateName: GATE,
    failuresOf: noFailures,
  });
  assert.deepEqual(outcome, { status: "green", healedAfter: 0, warnings: [] });
  assert.equal(healerRuns, 0);
  assert.deepEqual(suffixes, [""]);
});

test("a red verdict runs the healer with the parsed failures, then re-runs the gate", async () => {
  const { runGate, suffixes } = scriptedGate([red("/logs/first.log"), green()]);
  const inputs: HealAttemptInput[] = [];
  const outcome = await runGateWithHealing({
    runGate,
    runHealer: async (input) => {
      inputs.push(input);
    },
    maxAttempts: 2,
    gateName: GATE,
    failuresOf: (result) => [`Failed X (from ${result.logPath})`],
  });
  assert.equal(outcome.status, "green");
  assert.equal(outcome.status === "green" && outcome.healedAfter, 1);
  assert.equal(inputs.length, 1);
  assert.equal(inputs[0]!.attempt, 1);
  assert.equal(inputs[0]!.maxAttempts, 2);
  assert.equal(inputs[0]!.logPath, "/logs/first.log");
  assert.deepEqual(inputs[0]!.failures, ["Failed X (from /logs/first.log)"]);
  assert.deepEqual(suffixes, ["", healLogSuffix(1)]);
});

// The briefing must be parsed with the FAILED gate's parser: a build log and a
// test log carry failures in completely different shapes.
test("failuresOf receives the whole red verdict, parser included", async () => {
  const { runGate } = scriptedGate([
    { ok: false, failed: "build", parser: "dotnetBuild", logPath: "/logs/b.log" },
    green(),
  ]);
  const seen: Array<string | undefined> = [];
  await runGateWithHealing({
    runGate,
    runHealer: async () => {},
    maxAttempts: 1,
    gateName: "build",
    failuresOf: (result) => {
      seen.push(result.parser);
      return [];
    },
  });
  assert.deepEqual(seen, ["dotnetBuild"]);
});

test("stays red after every allowed attempt and reports the LAST verdict", async () => {
  const { runGate, suffixes } = scriptedGate([
    red("/logs/a.log"),
    red("/logs/b.log"),
    red("/logs/c.log"),
  ]);
  let healerRuns = 0;
  const outcome = await runGateWithHealing({
    runGate,
    runHealer: async () => {
      healerRuns += 1;
    },
    maxAttempts: 2,
    gateName: GATE,
    failuresOf: noFailures,
  });
  assert.equal(outcome.status, "red");
  assert.equal(outcome.status === "red" && outcome.attempts, 2);
  assert.equal(outcome.status === "red" && outcome.last.logPath, "/logs/c.log");
  assert.equal(healerRuns, 2);
  assert.deepEqual(suffixes, ["", "-heal-1", "-heal-2"]);
});

// The default for this repo: healing OFF, because the healer runs on the host
// with permission prompts bypassed.
test("maxAttempts 0 disables healing: the first red is final and no healer runs", async () => {
  const { runGate } = scriptedGate([red()]);
  let healerRuns = 0;
  const outcome = await runGateWithHealing({
    runGate,
    runHealer: async () => {
      healerRuns += 1;
    },
    maxAttempts: 0,
    gateName: GATE,
    failuresOf: noFailures,
  });
  assert.equal(outcome.status, "red");
  assert.equal(outcome.status === "red" && outcome.attempts, 0);
  assert.equal(healerRuns, 0);
});

// An unverified gate (Docker died, spawn error, timeout) has no defect to fix:
// a healer briefed with a thousand environment failures would "repair" sound code.
test("an unverified verdict is never healed", async () => {
  const { runGate } = scriptedGate([
    {
      ok: false,
      failed: GATE,
      unverified: "the Docker daemon is not reachable",
      logPath: "/logs/gate.log",
    },
  ]);
  let healerRuns = 0;
  const outcome = await runGateWithHealing({
    runGate,
    runHealer: async () => {
      healerRuns += 1;
    },
    maxAttempts: 2,
    gateName: GATE,
    failuresOf: noFailures,
  });
  assert.equal(outcome.status, "unverified");
  assert.equal(healerRuns, 0);
});

test("a user interrupt is never healed, even mid-sequence", async () => {
  const { runGate } = scriptedGate([
    red(),
    { ok: false, failed: GATE, interrupted: true, logPath: "/logs/x.log" },
  ]);
  let healerRuns = 0;
  const outcome = await runGateWithHealing({
    runGate,
    runHealer: async () => {
      healerRuns += 1;
    },
    maxAttempts: 3,
    gateName: GATE,
    failuresOf: noFailures,
  });
  assert.equal(outcome.status, "interrupted");
  assert.equal(healerRuns, 1);
});

test("a healer that throws ends the loop as healer-failed with the last red verdict", async () => {
  const { runGate, suffixes } = scriptedGate([red("/logs/a.log")]);
  const boom = new Error("merge-back onto the host branch failed");
  const outcome = await runGateWithHealing({
    runGate,
    runHealer: async () => {
      throw boom;
    },
    maxAttempts: 2,
    gateName: GATE,
    failuresOf: noFailures,
  });
  assert.equal(outcome.status, "healer-failed");
  assert.equal(outcome.status === "healer-failed" && outcome.attempt, 1);
  assert.equal(outcome.status === "healer-failed" && outcome.error, boom);
  assert.equal(outcome.status === "healer-failed" && outcome.last.logPath, "/logs/a.log");
  // The gate is NOT re-run after a healer crash — HEAD is unchanged and red.
  assert.deepEqual(suffixes, [""]);
});

test("gate warnings from every run are collected onto the outcome", async () => {
  const { runGate } = scriptedGate([
    { ok: false, failed: GATE, logPath: "/l", warning: "survivors A" },
    green("survivors B"),
  ]);
  const outcome = await runGateWithHealing({
    runGate,
    runHealer: async () => {},
    maxAttempts: 1,
    gateName: GATE,
    failuresOf: noFailures,
  });
  assert.deepEqual(outcome.warnings, ["survivors A", "survivors B"]);
});

test("onAttempt sees each briefing before its healer runs", async () => {
  const { runGate } = scriptedGate([red(), red(), green()]);
  const seen: number[] = [];
  const order: string[] = [];
  await runGateWithHealing({
    runGate,
    runHealer: async ({ attempt }) => {
      order.push(`heal-${attempt}`);
    },
    maxAttempts: 2,
    gateName: GATE,
    failuresOf: noFailures,
    onAttempt: ({ attempt }) => {
      seen.push(attempt);
      order.push(`brief-${attempt}`);
    },
  });
  assert.deepEqual(seen, [1, 2]);
  assert.deepEqual(order, ["brief-1", "heal-1", "brief-2", "heal-2"]);
});

test("rejects a negative or fractional maxAttempts", async () => {
  for (const bad of [-1, 1.5, Number.NaN]) {
    await assert.rejects(
      runGateWithHealing({
        runGate: async () => green(),
        runHealer: async () => {},
        maxAttempts: bad,
        gateName: "g",
        failuresOf: noFailures,
      }),
      /maxAttempts/,
    );
  }
});
