import assert from "node:assert/strict";
import test from "node:test";

import {
  defineConfig,
  formatGateCommands,
  gateCommand,
  gateNames,
  issueBranch,
  joinGateCommands,
  resolveLimits,
  resolveModels,
  type GateConfig,
  type LimitsConfig,
  type PhaseModel,
  type PhaseName,
  type SandcastleConfig,
} from "./config.mts";

// Neutral fixtures throughout: these tests must never encode the repo they
// happen to live in — that is the whole point of the config indirection.
const gate = (over: Partial<GateConfig> = {}): GateConfig => ({
  name: "build",
  file: "dotnet",
  args: ["build", "src/Acme.sln", "-warnaserror"],
  timeoutMs: 600_000,
  parser: "dotnetBuild",
  ...over,
});

const limits: LimitsConfig = {
  maxIterations: 40,
  maxConcurrentAgents: 3,
  plannerQueueDepth: 10,
  idleTimeoutSeconds: 3900,
  healAttempts: 0,
};

const models: Readonly<Record<PhaseName, PhaseModel>> = {
  planner: { model: "model-plan", effort: "medium" },
  implementer: { model: "model-impl", effort: "high" },
  reviewer: { model: "model-review", effort: "high" },
  merger: { model: "model-merge", effort: "xhigh" },
  healer: { model: "model-heal", effort: "xhigh" },
};

// --- gate command rendering ------------------------------------------------

test("gateCommand renders the executable and its arguments", () => {
  assert.equal(gateCommand(gate()), "dotnet build src/Acme.sln -warnaserror");
});

test("gateCommand quotes only the arguments a shell would mangle", () => {
  assert.equal(
    gateCommand(gate({ args: ["test", "--filter", "FullyQualifiedName~A B"] })),
    "dotnet test --filter 'FullyQualifiedName~A B'",
  );
  assert.equal(
    gateCommand(gate({ args: ["run", "it's"] })),
    `dotnet run 'it'\\''s'`,
  );
});

test("gateNames and joinGateCommands combine the whole ordered list", () => {
  const gates = [gate(), gate({ name: "tests", args: ["test", "src/Acme.sln"] })];

  assert.equal(gateNames(gates), "build + tests");
  assert.equal(
    joinGateCommands(gates),
    "dotnet build src/Acme.sln -warnaserror && dotnet test src/Acme.sln",
  );
});

test("formatGateCommands renders every gate with its name and an indented command", () => {
  const rendered = formatGateCommands([
    gate(),
    gate({ name: "tests", args: ["test", "src/Acme.sln"] }),
  ]);

  assert.equal(
    rendered,
    [
      "- **build**",
      "",
      "      dotnet build src/Acme.sln -warnaserror",
      "",
      "- **tests**",
      "",
      "      dotnet test src/Acme.sln",
    ].join("\n"),
  );
});

// A prompt with an empty {{GATE_COMMANDS}} would read as "run the gates:" with
// nothing after it; say so instead.
test("formatGateCommands says so when a repo configures no gates", () => {
  assert.match(formatGateCommands([]), /no gates are configured/);
});

test("the rendered commands never leak the config into the prompt verbatim", () => {
  // Sanity check on the contract: what a prompt receives is derived from the
  // gate list and nothing else.
  assert.equal(
    formatGateCommands([gate({ name: "lint", file: "pnpm", args: ["lint"] })]),
    "- **lint**\n\n      pnpm lint",
  );
});

// --- limits ----------------------------------------------------------------

test("resolveLimits keeps the configured values when nothing is overridden", () => {
  assert.deepEqual(resolveLimits(limits, {}), { ...limits, plannerQueueDepth: 10 });
});

test("resolveLimits treats a blank override as unset", () => {
  const resolved = resolveLimits(limits, {
    MAX_CONCURRENT_AGENTS: "   ",
    SANDCASTLE_HEAL_ATTEMPTS: "",
  });

  assert.equal(resolved.maxConcurrentAgents, 3);
  assert.equal(resolved.healAttempts, 0);
});

test("MAX_CONCURRENT_AGENTS overrides the configured cap", () => {
  assert.equal(resolveLimits(limits, { MAX_CONCURRENT_AGENTS: "6" }).maxConcurrentAgents, 6);
});

test("SANDCASTLE_HEAL_ATTEMPTS turns healing on for a single run", () => {
  assert.equal(resolveLimits(limits, { SANDCASTLE_HEAL_ATTEMPTS: "2" }).healAttempts, 2);
  // Zero stays legal: it is the default, and the way to turn healing back off.
  assert.equal(resolveLimits(limits, { SANDCASTLE_HEAL_ATTEMPTS: "0" }).healAttempts, 0);
});

// A typo that quietly restored the default would over-provision a machine
// sized for fewer agents and OOM-kill implementers mid-gate.
test("a malformed override throws instead of falling back silently", () => {
  assert.throws(
    () => resolveLimits(limits, { MAX_CONCURRENT_AGENTS: "0" }),
    /MAX_CONCURRENT_AGENTS must be an integer >= 1/,
  );
  assert.throws(
    () => resolveLimits(limits, { MAX_CONCURRENT_AGENTS: "two" }),
    /MAX_CONCURRENT_AGENTS/,
  );
  assert.throws(
    () => resolveLimits(limits, { SANDCASTLE_HEAL_ATTEMPTS: "-1" }),
    /SANDCASTLE_HEAL_ATTEMPTS must be an integer >= 0/,
  );
  assert.throws(
    () => resolveLimits(limits, { SANDCASTLE_HEAL_ATTEMPTS: "1.5" }),
    /SANDCASTLE_HEAL_ATTEMPTS/,
  );
});

// A queue no deeper than the cap is empty by construction: the pool it feeds
// would idle every freed slot until the slowest sibling ended.
test("the planner queue is clamped up to the concurrency cap, never below it", () => {
  const shallow = { ...limits, plannerQueueDepth: 2 };

  assert.equal(resolveLimits(shallow, {}).plannerQueueDepth, 3);
  assert.equal(
    resolveLimits(shallow, { MAX_CONCURRENT_AGENTS: "8" }).plannerQueueDepth,
    8,
  );
  // A queue already deeper than the cap is left alone.
  assert.equal(resolveLimits(limits, { MAX_CONCURRENT_AGENTS: "2" }).plannerQueueDepth, 10);
});

// --- models ----------------------------------------------------------------

test("resolveModels keeps the configured models when nothing is overridden", () => {
  const resolved = resolveModels(models, {});

  for (const [phase, configured] of Object.entries(models)) {
    assert.deepEqual(resolved[phase as PhaseName], { ...configured, overridden: [] });
  }
});

test("resolveModels treats a blank override as unset", () => {
  const resolved = resolveModels(models, {
    SANDCASTLE_PLANNER_MODEL: "   ",
    SANDCASTLE_PLANNER_EFFORT: "",
  });

  assert.deepEqual(resolved.planner, { ...models.planner, overridden: [] });
});

test("SANDCASTLE_<PHASE>_MODEL overrides that phase's model", () => {
  const resolved = resolveModels(models, { SANDCASTLE_IMPLEMENTER_MODEL: " claude-test-model " });

  assert.equal(resolved.implementer.model, "claude-test-model");
  assert.equal(resolved.implementer.effort, "high");
  assert.deepEqual(resolved.implementer.overridden, ["model"]);
});

test("SANDCASTLE_<PHASE>_EFFORT overrides that phase's effort", () => {
  const resolved = resolveModels(models, { SANDCASTLE_HEALER_EFFORT: "low" });

  assert.equal(resolved.healer.model, "model-heal");
  assert.equal(resolved.healer.effort, "low");
  assert.deepEqual(resolved.healer.overridden, ["effort"]);
});

test("the [1m] context selector is accepted as part of a model id", () => {
  const resolved = resolveModels(models, { SANDCASTLE_REVIEWER_MODEL: "claude-test-model[1m]" });
  assert.equal(resolved.reviewer.model, "claude-test-model[1m]");
});

test("the overridden list names every field that came from the environment", () => {
  const resolved = resolveModels(models, {
    SANDCASTLE_MERGER_MODEL: "claude-test-model",
    SANDCASTLE_MERGER_EFFORT: "max",
  });

  assert.deepEqual(resolved.merger, {
    model: "claude-test-model",
    effort: "max",
    overridden: ["model", "effort"],
  });
});

test("one phase's override leaves every other phase on its configured model", () => {
  const resolved = resolveModels(models, {
    SANDCASTLE_REVIEWER_MODEL: "claude-test-model",
    SANDCASTLE_REVIEWER_EFFORT: "low",
  });

  for (const phase of ["planner", "implementer", "merger", "healer"] as const) {
    assert.deepEqual(resolved[phase], { ...models[phase], overridden: [] });
  }
});

// A typo that quietly restored the config default would run a whole round on
// the wrong model or effort, with nothing on screen saying so.
test("a malformed effort throws, naming the variable and the allowed levels", () => {
  assert.throws(
    () => resolveModels(models, { SANDCASTLE_REVIEWER_EFFORT: "extreme" }),
    /SANDCASTLE_REVIEWER_EFFORT must be one of low, medium, high, xhigh, max/,
  );
  // Exact match only: the level reaches the agent CLI verbatim, so it is
  // checked verbatim — no case folding.
  assert.throws(
    () => resolveModels(models, { SANDCASTLE_REVIEWER_EFFORT: "HIGH" }),
    /SANDCASTLE_REVIEWER_EFFORT/,
  );
});

test("a model the agent CLI cannot route throws instead of falling back", () => {
  assert.throws(
    () => resolveModels(models, { SANDCASTLE_PLANNER_MODEL: "gpt-test-model" }),
    /SANDCASTLE_PLANNER_MODEL must be a Claude model id starting with "claude-"/,
  );
  assert.throws(
    () => resolveModels(models, { SANDCASTLE_PLANNER_MODEL: "claude-test model" }),
    /SANDCASTLE_PLANNER_MODEL.*no whitespace/,
  );
});

// --- branches --------------------------------------------------------------

test("issueBranch is the prefix and the id, with nothing added", () => {
  assert.equal(issueBranch({ integrationBranch: "trunk", branchPrefix: "agent/issue-" }, "42"), "agent/issue-42");
});

// --- defineConfig ----------------------------------------------------------

test("defineConfig returns the config unchanged", () => {
  const config: SandcastleConfig = {
    project: { name: "Acme" },
    issues: {
      label: "agent-ready",
      listArgs: ["issue", "list", "--state", "open"],
      plannerListCommand: "gh issue list --state open",
      closeComment: "done",
    },
    git: { integrationBranch: "trunk", branchPrefix: "agent/issue-" },
    gates: [gate()],
    limits,
    models: {
      planner: { model: "m", effort: "medium" },
      implementer: { model: "m", effort: "high" },
      reviewer: { model: "m", effort: "medium" },
      merger: { model: "m", effort: "xhigh" },
      healer: { model: "m", effort: "xhigh" },
    },
    sandbox: {
      cacheRoot: "~/.cache",
      caches: [],
      env: {},
      hostEnv: {},
      sentinelPort: 17032,
    },
    prompts: {
      plan: "./p.md",
      implement: "./i.md",
      review: "./r.md",
      merge: "./m.md",
      heal: "./h.md",
    },
  };

  assert.equal(defineConfig(config), config);
});
