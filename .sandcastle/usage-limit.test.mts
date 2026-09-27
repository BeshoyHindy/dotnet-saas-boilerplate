import assert from "node:assert/strict";
import test from "node:test";

import {
  checkUsageProbe,
  classifyProbeOutput,
  createUsageWait,
  probeArgs,
  type Probe,
  type ProbeOutcome,
  type UsageWaitOptions,
} from "./usage-limit.mts";

const MIN = 60_000;
const HOUR = 60 * MIN;

const answered: ProbeOutcome = { verdict: "answered" };
const limited: ProbeOutcome = { verdict: "limited", detail: "usage limit reached" };
const unavailable: ProbeOutcome = { verdict: "unavailable", detail: "not logged in" };

/**
 * A fake clock whose sleep advances time instantly, plus a probe scripted per
 * model. Each probe call takes the next verdict for its model; the last one
 * repeats once the script runs out.
 */
function harness(
  scripts: Record<string, ProbeOutcome[]>,
  over: Partial<UsageWaitOptions> = {},
) {
  let clock = 0;
  const probes: { model: string; at: number }[] = [];
  const lines: string[] = [];
  const probe: Probe = async (model) => {
    probes.push({ model, at: clock });
    const script = scripts[model];
    if (script === undefined || script.length === 0) {
      throw new Error(`no probe script for ${model}`);
    }
    return script.length > 1 ? script.shift()! : script[0]!;
  };
  const options: UsageWaitOptions = {
    probe,
    sleep: async (ms) => {
      clock += ms;
    },
    now: () => clock,
    log: (line) => lines.push(line),
    pollMs: 5 * MIN,
    maxWaitMs: 6 * HOUR,
    ...over,
  };
  return {
    wait: createUsageWait(options),
    probes,
    lines,
    now: () => clock,
    advance: (ms: number) => {
      clock += ms;
    },
  };
}

/** A phase that throws `errors` in order, then returns `value`. */
function phase<T>(value: T, ...errors: Error[]) {
  let calls = 0;
  const fn = async () => {
    calls += 1;
    const error = errors.shift();
    if (error !== undefined) {
      throw error;
    }
    return value;
  };
  return { fn, calls: () => calls };
}

// --- detection -------------------------------------------------------------

test("a phase that succeeds never probes", async () => {
  const { wait, probes } = harness({ m: [limited] });
  const { fn, calls } = phase("done");

  assert.equal(await wait.run("planner", "m", fn), "done");
  assert.equal(calls(), 1);
  assert.equal(probes.length, 0);
});

test("a real failure is re-thrown unchanged, with no wait", async () => {
  const { wait, probes, now } = harness({ m: [answered] });
  const boom = new Error("the agent crashed");
  const { fn, calls } = phase("never", boom);

  await assert.rejects(wait.run("merger", "m", fn), (error) => error === boom);
  assert.equal(calls(), 1);
  assert.equal(probes.length, 1);
  assert.equal(now(), 0);
});

// The error text moves between CLI releases and often never reaches the error
// at all, so it is never read: only the probe decides.
test("an error that says 'usage limit' is still re-thrown when the model answers", async () => {
  const { wait } = harness({ m: [answered] });
  const looksLimited = new Error("claude exited with code 1:\nClaude usage limit reached");
  const { fn, calls } = phase("never", looksLimited);

  await assert.rejects(wait.run("reviewer", "m", fn), (error) => error === looksLimited);
  assert.equal(calls(), 1);
});

test("a failure while the model is not answering waits, then re-runs the phase", async () => {
  const { wait, probes, now, lines } = harness({
    m: [unavailable, limited, unavailable, answered],
  });
  const { fn, calls } = phase("merged", new Error("exit 1"));

  assert.equal(await wait.run("merger", "m", fn), "merged");
  assert.equal(calls(), 2);
  // One probe to diagnose the failure, then one per poll until it answers.
  assert.deepEqual(
    probes.map((p) => p.at),
    [0, 5 * MIN, 10 * MIN, 15 * MIN],
  );
  assert.equal(now(), 15 * MIN);
  assert.match(lines.join("\n"), /merger: failed and m is not answering/);
  assert.match(lines.join("\n"), /merger: m answers again — re-running the phase/);
});

test("the failing phase's own model is the one probed", async () => {
  const { wait, probes } = harness({
    "model-impl": [limited, answered],
    "model-plan": [answered],
  });
  const { fn } = phase(1, new Error("exit 1"));

  await wait.run("#7 implementer", "model-impl", fn);
  assert.deepEqual(new Set(probes.map((p) => p.model)), new Set(["model-impl"]));
});

test("a probe that throws counts as not answering", async () => {
  let calls = 0;
  const { wait } = harness(
    {},
    {
      probe: async () => {
        calls += 1;
        if (calls === 1) {
          throw new Error("spawn claude ENOENT");
        }
        return answered;
      },
    },
  );
  const { fn } = phase("ok", new Error("exit 1"));

  assert.equal(await wait.run("healer-1", "m", fn), "ok");
  assert.equal(calls, 2);
});

// --- budget ----------------------------------------------------------------

test("the wait gives up at its budget and re-throws the original error", async () => {
  const { wait, now, lines } = harness({ m: [limited] }, { maxWaitMs: HOUR });
  const boom = new Error("exit 1");
  const { fn, calls } = phase("never", boom);

  await assert.rejects(wait.run("planner", "m", fn), (error) => error === boom);
  assert.equal(calls(), 1);
  assert.equal(now(), HOUR);
  assert.match(lines.at(-1)!, /did not answer within the 60 min usage-limit wait/);
});

// An implementer that worked for hours before the limit still gets the whole
// wait: the budget is counted from the first limit hit, not from the start.
test("the budget starts at the first limit hit, not when the phase started", async () => {
  const { wait, advance, now } = harness(
    // Diagnose, then 6 polls of 5 min: answers 30 min after the hit.
    { m: [limited, limited, limited, limited, limited, limited, answered] },
    { maxWaitMs: HOUR },
  );
  let calls = 0;
  const result = await wait.run("#3 implementer", "m", async () => {
    calls += 1;
    if (calls === 1) {
      advance(10 * HOUR); // hours of real work, then the limit
      throw new Error("exit 1");
    }
    return "committed";
  });

  assert.equal(result, "committed");
  assert.equal(now(), 10 * HOUR + 30 * MIN);
});

// A phase that keeps failing cannot loop forever: every retry of one call
// draws on the same budget.
test("retries of one call share its budget", async () => {
  const { wait, now } = harness(
    // First hit: answers after 40 min. Second hit: never answers again.
    { m: [limited, ...Array(7).fill(limited), answered, limited] },
    { maxWaitMs: HOUR },
  );
  const boom = new Error("second failure");
  const { fn, calls } = phase("never", new Error("first failure"), boom);

  await assert.rejects(wait.run("reviewer", "m", fn), (error) => error === boom);
  assert.equal(calls(), 2);
  // Given up one hour after the FIRST hit, not a fresh hour after the second.
  assert.equal(now(), HOUR);
});

test("each call gets a budget of its own", async () => {
  const { wait, now } = harness(
    { m: [limited, limited, answered, limited, limited, answered] },
    { maxWaitMs: 15 * MIN },
  );

  await wait.run("planner", "m", phase("a", new Error("x")).fn);
  assert.equal(now(), 10 * MIN);
  await wait.run("merger", "m", phase("b", new Error("y")).fn);
  assert.equal(now(), 20 * MIN);
});

test("a budget of 0 turns the wait off and spends no probe", async () => {
  const { wait, probes } = harness({ m: [limited] }, { maxWaitMs: 0 });
  const boom = new Error("exit 1");

  assert.equal(wait.enabled, false);
  await assert.rejects(wait.run("planner", "m", phase("x", boom).fn), (error) => error === boom);
  assert.equal(probes.length, 0);
});

// --- one shared wait per model ---------------------------------------------

test("concurrent phases on one model share one probe loop", async () => {
  const { wait, probes } = harness({ m: [limited, limited, limited, answered] });
  const phases = [1, 2, 3].map((n) => phase(`issue ${n}`, new Error(`exit ${n}`)));

  const results = await Promise.all(
    phases.map((p, i) => wait.run(`#${i + 1} implementer`, "m", p.fn)),
  );

  assert.deepEqual(results, ["issue 1", "issue 2", "issue 3"]);
  assert.deepEqual(phases.map((p) => p.calls()), [2, 2, 2]);
  // One diagnosis and three polls in all — not one loop per phase.
  assert.equal(probes.length, 4);
});

test("a model that answers is not held up by another model's wait", async () => {
  const { wait, probes } = harness({ spent: [limited], fine: [answered] });
  const real = new Error("a real failure");

  // Runs out its own budget; the rejection is awaited at the end.
  const waiting = assert.rejects(
    wait.run("#1 implementer", "spent", phase("x", new Error("exit")).fn),
  );
  const merger = phase("y", real);
  await assert.rejects(wait.run("merger", "fine", merger.fn), (error) => error === real);

  // Diagnosed by one probe of its own model, at once, and never re-run.
  assert.deepEqual(
    probes.filter((p) => p.model === "fine"),
    [{ model: "fine", at: 0 }],
  );
  assert.equal(merger.calls(), 1);
  await waiting;
});

// --- probe output ----------------------------------------------------------

test("the probe runs one turn with no tools, no MCP servers and no saved session", () => {
  const args = probeArgs("claude-model-x");

  assert.equal(args[0], "-p");
  assert.equal(args[args.indexOf("--model") + 1], "claude-model-x");
  assert.equal(args[args.indexOf("--output-format") + 1], "json");
  assert.equal(args[args.indexOf("--max-turns") + 1], "1");
  assert.equal(args[args.indexOf("--tools") + 1], "");
  assert.ok(args.includes("--strict-mcp-config"));
  assert.ok(args.includes("--no-session-persistence"));
});

test("an array of stream events with a successful result is an answer", () => {
  const stdout = JSON.stringify([
    { type: "system", subtype: "init" },
    { type: "assistant", message: { content: [{ type: "text", text: "OK" }] } },
    { type: "result", subtype: "success", is_error: false, result: "OK" },
  ]);
  assert.deepEqual(classifyProbeOutput(stdout), answered);
});

test("a bare result object is an answer too", () => {
  const stdout = JSON.stringify({ type: "result", is_error: false, result: "OK" });
  assert.deepEqual(classifyProbeOutput(stdout), answered);
});

test("an error result that names a limit, or carries a 429, is a spent quota", () => {
  const named = JSON.stringify([
    { type: "result", is_error: true, result: "Claude AI usage limit reached|1790478000" },
  ]);
  const status = JSON.stringify({
    type: "result",
    is_error: true,
    api_error_status: 429,
    result: "API Error",
  });

  assert.equal(classifyProbeOutput(named, "exit code 1").verdict, "limited");
  assert.equal(classifyProbeOutput(status, "exit code 1").verdict, "limited");
});

test("a login error is not a spent quota", () => {
  const stdout = JSON.stringify({
    type: "result",
    is_error: true,
    result: "Invalid API key · Please run /login",
  });
  assert.deepEqual(classifyProbeOutput(stdout, "exit code 1"), {
    verdict: "unavailable",
    detail: "Invalid API key · Please run /login",
  });
});

test("output that is not JSON, or holds no result, is not an answer", () => {
  assert.deepEqual(classifyProbeOutput("", "spawn claude ENOENT"), {
    verdict: "unavailable",
    detail: "spawn claude ENOENT",
  });
  assert.equal(
    classifyProbeOutput("error: unknown option '--no-session-persistence'").verdict,
    "unavailable",
  );
  assert.equal(
    classifyProbeOutput(JSON.stringify([{ type: "system", subtype: "init" }])).verdict,
    "unavailable",
  );
});

// --- startup self-check ----------------------------------------------------

test("the self-check keeps the wait on when the startup probe answers", async () => {
  const lines: string[] = [];
  const on = await checkUsageProbe({
    probe: async () => answered,
    model: "probe-model",
    maxWaitMs: HOUR,
    log: (line) => lines.push(line),
  });

  assert.equal(on, true);
  assert.match(lines.join("\n"), /Usage-limit wait: ON/);
});

test("the self-check turns the wait off, loudly, when the host CLI cannot probe", async () => {
  const lines: string[] = [];
  const on = await checkUsageProbe({
    probe: async () => unavailable,
    model: "probe-model",
    maxWaitMs: HOUR,
    log: (line) => lines.push(line),
  });

  assert.equal(on, false);
  assert.match(lines.join("\n"), /Usage-limit wait: OFF for this run/);
  assert.match(lines.join("\n"), /not logged in/);
});

test("a startup probe that throws turns the wait off", async () => {
  const on = await checkUsageProbe({
    probe: async () => {
      throw new Error("spawn claude ENOENT");
    },
    model: "probe-model",
    maxWaitMs: HOUR,
    log: () => {},
  });
  assert.equal(on, false);
});

test("a wait budget of 0 is off before any probe is spent", async () => {
  let probes = 0;
  const lines: string[] = [];
  const on = await checkUsageProbe({
    probe: async () => {
      probes += 1;
      return answered;
    },
    model: "probe-model",
    maxWaitMs: 0,
    log: (line) => lines.push(line),
  });

  assert.equal(on, false);
  assert.equal(probes, 0);
  assert.match(lines.join("\n"), /OFF \(the wait budget is 0\)/);
});

// --- starting inside a limit -----------------------------------------------

test("a run started inside a limit keeps the wait on", async () => {
  const lines: string[] = [];
  const on = await checkUsageProbe({
    probe: async () => limited,
    model: "probe-model",
    maxWaitMs: HOUR,
    log: (line) => lines.push(line),
  });

  assert.equal(on, true);
  assert.match(lines.join("\n"), /quota is spent right now/);
});

test("started inside a limit, the first phase waits for the reset and resumes", async () => {
  // The startup probe is refused; the phase's own diagnosis and first poll are
  // still inside the limit, and the second poll finds it reset.
  const { wait, now } = harness({ m: [limited, limited, answered] });
  const on = await checkUsageProbe({
    probe: async () => limited,
    model: "m",
    maxWaitMs: 6 * HOUR,
    log: () => {},
  });
  assert.equal(on, true);

  const { fn, calls } = phase({ issues: [] }, new Error("claude exited with code 1"));
  assert.deepEqual(await wait.run("planner", "m", fn), { issues: [] });
  assert.equal(calls(), 2);
  assert.equal(now(), 10 * MIN);
});
