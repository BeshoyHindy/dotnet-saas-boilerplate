import assert from "node:assert/strict";
import test from "node:test";

import { resolveLimits, type SandcastleConfig } from "./config.mts";
import {
  isDryRun,
  listAgentIssues,
  renderDryRun,
  type CommandExec,
} from "./dry-run.mts";

// A neutral fixture config: these tests must pass unchanged in any repository
// that adopts the pipeline.
const config: SandcastleConfig = {
  project: { name: "Acme" },
  issues: {
    label: "agent-ready",
    listArgs: ["issue", "list", "--state", "open", "--label", "agent-ready", "--json", "number,title"],
    plannerListCommand: "gh issue list --state open --label agent-ready",
    closeComment: "Completed by Sandcastle",
  },
  git: { integrationBranch: "trunk", branchPrefix: "agent/issue-" },
  gates: [
    {
      name: "build",
      file: "dotnet",
      args: ["build", "src/Acme.sln", "-warnaserror"],
      timeoutMs: 15 * 60_000,
      parser: "dotnetBuild",
    },
    {
      name: "tests",
      file: "dotnet",
      args: ["test", "src/Acme.sln"],
      timeoutMs: 45 * 60_000,
      parser: "dotnetTest",
      requiresDocker: true,
    },
  ],
  limits: {
    maxIterations: 40,
    maxConcurrentAgents: 3,
    plannerQueueDepth: 10,
    idleTimeoutSeconds: 3900,
    healAttempts: 0,
  },
  models: {
    planner: { model: "model-planner" },
    implementer: { model: "model-impl" },
    reviewer: { model: "model-review", effort: "high" },
    merger: { model: "model-merge" },
    healer: { model: "model-heal" },
  },
  sandbox: {
    cacheRoot: "~/.sandcastle-cache",
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

const limits = resolveLimits(config.limits, {});

// --- isDryRun --------------------------------------------------------------

test("--dry-run on the command line or SANDCASTLE_DRY_RUN=1 both count", () => {
  assert.equal(isDryRun(["node", "main.mts", "--dry-run"], {}), true);
  assert.equal(isDryRun([], { SANDCASTLE_DRY_RUN: "1" }), true);
  assert.equal(isDryRun([], {}), false);
  assert.equal(isDryRun([], { SANDCASTLE_DRY_RUN: "0" }), false);
  assert.equal(isDryRun(["--dryrun"], {}), false);
});

// --- listAgentIssues -------------------------------------------------------

const ghReturning =
  (stdout: string, seen?: Array<{ file: string; args: readonly string[] }>): CommandExec =>
  (file, args) => {
    seen?.push({ file, args });
    return stdout;
  };

test("passes the configured query straight to gh, with no shell in between", () => {
  const seen: Array<{ file: string; args: readonly string[] }> = [];

  listAgentIssues(config.issues.listArgs, ghReturning("[]", seen));

  assert.deepEqual(seen, [{ file: "gh", args: config.issues.listArgs }]);
});

test("parses the issues gh reports", () => {
  const query = listAgentIssues(
    config.issues.listArgs,
    ghReturning(JSON.stringify([{ number: 4, title: "Port the pipeline", labels: [] }])),
  );

  assert.deepEqual(query, { ok: true, issues: [{ number: 4, title: "Port the pipeline" }] });
});

test("an empty list is a legitimate answer, not a failure", () => {
  assert.deepEqual(listAgentIssues(config.issues.listArgs, ghReturning("[]")), {
    ok: true,
    issues: [],
  });
  assert.deepEqual(listAgentIssues(config.issues.listArgs, ghReturning("   ")), {
    ok: true,
    issues: [],
  });
});

// "gh is not authenticated" and "there is no work" must never look the same on
// the screen a human reads before starting a night of agent runs.
test("a gh failure is reported, never flattened into an empty list", () => {
  const query = listAgentIssues(config.issues.listArgs, () => {
    throw new Error("gh: not authenticated");
  });

  assert.equal(query.ok, false);
  assert.match(query.ok === false ? query.error : "", /not authenticated/);
});

test("output that is not an issue array is reported as such", () => {
  for (const [stdout, pattern] of [
    ["not json at all", /did not return JSON/],
    ['{"issues":[]}', /not an array/],
    ["[1,2]", /not an object/],
    ['[{"title":"no number"}]', /number and a title/],
    ['[{"number":1}]', /number and a title/],
  ] as const) {
    const query = listAgentIssues(config.issues.listArgs, ghReturning(stdout));
    assert.equal(query.ok, false, stdout);
    assert.match(query.ok === false ? query.error : "", pattern);
  }
});

// --- renderDryRun ----------------------------------------------------------

test("the report names the resolved config, not the defaults in the code", () => {
  const report = renderDryRun(config, limits, { ok: true, issues: [] });

  assert.match(report, /Sandcastle dry run — Acme/);
  assert.match(report, /integration branch\s+trunk/);
  assert.match(report, /issue branches\s+agent\/issue-<issue>/);
  assert.match(report, /issue label\s+agent-ready/);
  assert.match(report, /concurrent agents\s+3/);
  assert.match(report, /planner queue depth\s+10/);
  assert.match(report, /model-review \(effort: high\)/);
});

test("the report states plainly that nothing was acquired", () => {
  const report = renderDryRun(config, limits, { ok: true, issues: [] });
  assert.match(report, /no sandbox, no model session, no sentinel port/);
});

test("healing off is called out, and a raised attempt count is not", () => {
  assert.match(renderDryRun(config, limits, { ok: true, issues: [] }), /healing is OFF/);
  const healing = resolveLimits(config.limits, { SANDCASTLE_HEAL_ATTEMPTS: "2" });
  assert.doesNotMatch(renderDryRun(config, healing, { ok: true, issues: [] }), /healing is OFF/);
  assert.match(renderDryRun(config, healing, { ok: true, issues: [] }), /healing attempts\s+2/);
});

test("the report shows each gate's real command, timeout and parser", () => {
  const report = renderDryRun(config, limits, { ok: true, issues: [] });

  assert.match(report, /\[build\] dotnet build src\/Acme\.sln -warnaserror/);
  assert.match(report, /timeout 15 min · parser dotnetBuild/);
  assert.match(report, /\[tests\] dotnet test src\/Acme\.sln/);
  assert.match(report, /timeout 45 min · parser dotnetTest · needs Docker/);
});

test("the report shows the generated gate commands exactly as the prompts get them", () => {
  const report = renderDryRun(config, limits, { ok: true, issues: [] });

  assert.match(report, /\{\{GATE_COMMANDS\}\} as the prompts will receive it/);
  assert.match(report, /- \*\*build\*\*/);
  assert.match(report, /dotnet test src\/Acme\.sln/);
});

test("the report lists the open issues and the branch each would get", () => {
  const report = renderDryRun(config, limits, {
    ok: true,
    issues: [
      { number: 4, title: "Port the pipeline" },
      { number: 9, title: "Add a gate" },
    ],
  });

  assert.match(report, /#4 Port the pipeline → agent\/issue-4/);
  assert.match(report, /#9 Add a gate → agent\/issue-9/);
  assert.match(report, /2 issue\(s\) visible/);
});

test("no open issues is spelt out as such", () => {
  const report = renderDryRun(config, limits, { ok: true, issues: [] });
  assert.match(report, /\(none — a real run would plan nothing and exit\)/);
});

test("a failed listing is loud in the report and blames nothing on the backlog", () => {
  const report = renderDryRun(config, limits, { ok: false, error: "gh: not authenticated" });

  assert.match(report, /COULD NOT LIST THEM: gh: not authenticated/);
  assert.match(report, /A real run's planner would fail the same way/);
  assert.doesNotMatch(report, /would plan nothing and exit/);
});

test("a repo with no gates is told its merged HEAD would go unverified", () => {
  const report = renderDryRun({ ...config, gates: [] }, limits, { ok: true, issues: [] });
  assert.match(report, /none configured — the merged HEAD would never be verified/);
});
