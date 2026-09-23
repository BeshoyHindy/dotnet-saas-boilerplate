import assert from "node:assert/strict";
import test from "node:test";

import { resolveLimits, resolveModels, type SandcastleConfig } from "./config.mts";
import {
  isDryRun,
  listAgentIssues,
  parseIssueListing,
  renderDryRun,
  type CommandExec,
} from "./dry-run.mts";

// A neutral fixture config: these tests must pass unchanged in any repository
// that adopts the pipeline.
const config: SandcastleConfig = {
  project: { name: "Acme" },
  issues: {
    label: "agent-ready",
    listArgs: ["api", "graphql", "-f", "query=query{issues{number title blockedBy}}"],
    plannerListCommand: "gh api graphql -f query=query{issues{number title blockedBy}}",
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
    planner: { model: "model-planner", effort: "medium" },
    implementer: { model: "model-impl", effort: "high" },
    reviewer: { model: "model-review", effort: "high" },
    merger: { model: "model-merge", effort: "xhigh" },
    healer: { model: "model-heal", effort: "xhigh" },
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
const models = resolveModels(config.models, {});

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

/** A GraphQL issues connection, as `gh api graphql` prints it. */
const listing = (nodes: ReadonlyArray<Record<string, unknown>>): string =>
  JSON.stringify({ data: { repository: { issues: { nodes } } } });

test("passes the configured query straight to gh, with no shell in between", () => {
  const seen: Array<{ file: string; args: readonly string[] }> = [];

  listAgentIssues(config.issues.listArgs, ghReturning("[]", seen));

  assert.deepEqual(seen, [{ file: "gh", args: config.issues.listArgs }]);
});

test("parses the issues gh reports", () => {
  const query = listAgentIssues(
    config.issues.listArgs,
    ghReturning(listing([{ number: 4, title: "Port the pipeline", blockedBy: { nodes: [] } }])),
  );

  assert.deepEqual(query, {
    ok: true,
    issues: [{ number: 4, title: "Port the pipeline", blockedBy: [] }],
  });
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

// --- parseIssueListing: native blocking edges ------------------------------

test("an issue's OPEN blockers are kept and its closed ones dropped", () => {
  const query = parseIssueListing(
    listing([
      {
        number: 6,
        title: "Prune the packages",
        blockedBy: {
          nodes: [
            { number: 5, state: "OPEN" },
            { number: 4, state: "CLOSED" },
          ],
        },
      },
    ]),
  );

  assert.deepEqual(query, {
    ok: true,
    issues: [{ number: 6, title: "Prune the packages", blockedBy: [5] }],
  });
});

test("an issue with no dependency edges at all is unblocked", () => {
  const query = parseIssueListing(
    listing([{ number: 33, title: "Add a gate", blockedBy: { nodes: [] } }]),
  );

  assert.deepEqual(query, {
    ok: true,
    issues: [{ number: 33, title: "Add a gate", blockedBy: [] }],
  });
});

// Fail-closed: a query that stopped returning blocker edges would otherwise
// report the whole backlog as unblocked and queue issues whose prerequisites
// have not landed.
test("a listing without blocker edges is reported, not read as unblocked", () => {
  const query = parseIssueListing(listing([{ number: 6, title: "Prune the packages" }]));

  assert.equal(query.ok, false);
  assert.match(query.ok === false ? query.error : "", /blockedBy/);
});

test("a blocker without a number and a state is reported, never skipped", () => {
  for (const blockedBy of [
    { nodes: [{ state: "OPEN" }] },
    { nodes: [{ number: 5 }] },
    { nodes: "5" },
  ]) {
    const query = parseIssueListing(listing([{ number: 6, title: "Prune", blockedBy }]));
    assert.equal(query.ok, false, JSON.stringify(blockedBy));
    assert.match(query.ok === false ? query.error : "", /blocker/);
  }
});

test("a GraphQL error envelope is reported, not read as an empty backlog", () => {
  const query = parseIssueListing(
    JSON.stringify({ errors: [{ message: "Field 'blockedBy' doesn't exist on type 'Issue'" }] }),
  );

  assert.equal(query.ok, false);
  assert.match(query.ok === false ? query.error : "", /blockedBy' doesn't exist/);
});

// --- renderDryRun ----------------------------------------------------------

test("the report names the resolved config, not the defaults in the code", () => {
  const report = renderDryRun(config, limits, models, { ok: true, issues: [] });

  assert.match(report, /Sandcastle dry run — Acme/);
  assert.match(report, /integration branch\s+trunk/);
  assert.match(report, /issue branches\s+agent\/issue-<issue>/);
  assert.match(report, /issue label\s+agent-ready/);
  assert.match(report, /concurrent agents\s+3/);
  assert.match(report, /planner queue depth\s+10/);
  assert.match(report, /model-review \(effort: high\)/);
});

test("the report states plainly that nothing was acquired", () => {
  const report = renderDryRun(config, limits, models, { ok: true, issues: [] });
  assert.match(report, /no sandbox, no model session, no sentinel port/);
});

test("healing off is called out, and a raised attempt count is not", () => {
  assert.match(renderDryRun(config, limits, models, { ok: true, issues: [] }), /healing is OFF/);
  const healing = resolveLimits(config.limits, { SANDCASTLE_HEAL_ATTEMPTS: "2" });
  assert.doesNotMatch(renderDryRun(config, healing, models, { ok: true, issues: [] }), /healing is OFF/);
  assert.match(renderDryRun(config, healing, models, { ok: true, issues: [] }), /healing attempts\s+2/);
});

test("the report shows each gate's real command, timeout and parser", () => {
  const report = renderDryRun(config, limits, models, { ok: true, issues: [] });

  assert.match(report, /\[build\] dotnet build src\/Acme\.sln -warnaserror/);
  assert.match(report, /timeout 15 min · parser dotnetBuild/);
  assert.match(report, /\[tests\] dotnet test src\/Acme\.sln/);
  assert.match(report, /timeout 45 min · parser dotnetTest · needs Docker/);
});

test("the report shows the generated gate commands exactly as the prompts get them", () => {
  const report = renderDryRun(config, limits, models, { ok: true, issues: [] });

  assert.match(report, /\{\{GATE_COMMANDS\}\} as the prompts will receive it/);
  assert.match(report, /- \*\*build\*\*/);
  assert.match(report, /dotnet test src\/Acme\.sln/);
});

test("the report lists the open issues and the branch each would get", () => {
  const report = renderDryRun(config, limits, models, {
    ok: true,
    issues: [
      { number: 4, title: "Port the pipeline", blockedBy: [] },
      { number: 9, title: "Add a gate", blockedBy: [] },
    ],
  });

  assert.match(report, /#4 Port the pipeline → agent\/issue-4/);
  assert.match(report, /#9 Add a gate → agent\/issue-9/);
  assert.match(report, /2 issue\(s\) visible/);
});

test("the report marks each issue blocked or unblocked and names the blockers", () => {
  const report = renderDryRun(config, limits, models, {
    ok: true,
    issues: [
      { number: 4, title: "Port the pipeline", blockedBy: [] },
      { number: 9, title: "Add a gate", blockedBy: [4, 7] },
    ],
  });

  assert.match(report, /#4 Port the pipeline → agent\/issue-4 · unblocked/);
  assert.match(report, /#9 Add a gate → agent\/issue-9 · BLOCKED by #4, #7/);
  assert.match(report, /2 issue\(s\) visible, 1 unblocked/);
});

test("no open issues is spelt out as such", () => {
  const report = renderDryRun(config, limits, models, { ok: true, issues: [] });
  assert.match(report, /\(none — a real run would plan nothing and exit\)/);
});

test("a failed listing is loud in the report and blames nothing on the backlog", () => {
  const report = renderDryRun(config, limits, models, { ok: false, error: "gh: not authenticated" });

  assert.match(report, /COULD NOT LIST THEM: gh: not authenticated/);
  assert.match(report, /A real run's planner would fail the same way/);
  assert.doesNotMatch(report, /would plan nothing and exit/);
});

test("a repo with no gates is told its merged HEAD would go unverified", () => {
  const report = renderDryRun({ ...config, gates: [] }, limits, models, { ok: true, issues: [] });
  assert.match(report, /none configured — the merged HEAD would never be verified/);
});

// An `.env` override changes what a round runs on; the dry run is where a human
// sees that before committing a night of agent time to it.
test("the report marks each model field that came from the environment", () => {
  const overridden = resolveModels(config.models, {
    SANDCASTLE_REVIEWER_EFFORT: "max",
    SANDCASTLE_MERGER_MODEL: "claude-other",
    SANDCASTLE_MERGER_EFFORT: "low",
  });
  const report = renderDryRun(config, limits, overridden, { ok: true, issues: [] });

  assert.match(report, /reviewer\s+model-review \(effort: max\)  \[\.env: effort\]/);
  assert.match(report, /merger\s+claude-other \(effort: low\)  \[\.env: model, effort\]/);
  assert.match(report, /planner\s+model-planner \(effort: medium\)$/m);
});

test("no marker is shown when nothing is overridden", () => {
  const report = renderDryRun(config, limits, models, { ok: true, issues: [] });
  assert.doesNotMatch(report, /\[\.env:/);
});
