// The ONE repo-specific file of the Sandcastle agent pipeline (ADR-0006).
//
// Everything under `.sandcastle/` is generic, dependency-injected code with
// unit tests that know nothing about this repository. This file supplies the
// project name, the issue label and queries, the branch names, the gate
// commands, the models, the limits and the sandbox shape. Rename the project,
// swap the stack, or point the pipeline at another repo, and this is the only
// file that changes.
//
// The prompts under `.sandcastle/*.md` receive the gate commands as a
// generated `{{GATE_COMMANDS}}` argument, so they never hard-code one either.
//
// Check a change here with `pnpm sandcastle --dry-run`: it resolves this file,
// prints the gate commands the prompts will get and the issues the planner
// would see, and starts nothing.

import { defineConfig } from "./.sandcastle/config.mts";

// The issue listing, as GraphQL rather than `gh issue list --json`.
//
// This repo expresses "A blocks B" through GitHub's NATIVE issue dependencies,
// and `gh issue list` cannot report them at all; the REST summary can, but only
// one issue per request. GraphQL's `blockedBy` connection returns every edge —
// with each blocker's state, so closed ones can be dropped — in the SAME round
// trip as the issues, which is why both listings below are built from it.
//
// `{owner}` / `{repo}` are gh's own placeholders, resolved from the checkout's
// remote, so the query names no repository. `labels` and `states` do the
// filtering server-side, exactly as the `--label`/`--state` flags used to.
const issueListQuery = (fields: string): string =>
  `query($owner:String!,$name:String!){repository(owner:$owner,name:$name){` +
  `issues(first:100,states:OPEN,labels:["ready-for-agent"]){nodes{${fields} ` +
  `blockedBy(first:50){nodes{number state}}}}}}`;

// Model catalog — swap models by editing `models` below; never hardcode an id
// at a call site. Every phase runs on Claude Code, which is baked into
// `.sandcastle/Dockerfile` and authenticates with CLAUDE_CODE_OAUTH_TOKEN /
// ANTHROPIC_API_KEY. A "[1m]" suffix is the Claude Code 1M-context model
// selector. It is NOT free: once a session's context passes 200K tokens every
// request is billed at the long-context premium, so a role only gets "[1m]"
// when it genuinely reads that much (a reviewer over a big diff, a planner
// over the whole backlog).
const CLAUDE = {
  fable51: "claude-fable-5-1", // most capable; 200K + auto-compaction
  fable51Long: "claude-fable-5-1[1m]", // same model, 1M context
  opus5: "claude-opus-5", // strong all-rounder; 200K + auto-compaction
  opus5Long: "claude-opus-5[1m]", // same model, 1M context
  sonnet5: "claude-sonnet-5", // best speed/intelligence balance
} as const;

export default defineConfig({
  project: {
    // Used in agent briefings only; it is never parsed.
    name: "Boilerplate",
  },

  issues: {
    // Triage vocabulary: see docs/agents/triage-labels.md. Only issues a human
    // has judged fully specified reach an autonomous agent.
    label: "ready-for-agent",

    // The host-side listing behind `--dry-run`. Run WITHOUT a shell, so keep it
    // to plain `gh` arguments — no pipes, no `--jq`: the raw GraphQL response
    // is parsed (and its closed blockers dropped) by `.sandcastle/dry-run.mts`.
    listArgs: [
      "api",
      "graphql",
      "-F",
      "owner={owner}",
      "-F",
      "name={repo}",
      "-f",
      `query=${issueListQuery("number title")}`,
    ],

    // The planner's own query, executed inside its prompt. Bodies and comments
    // are what dependency reasoning needs, and they are far too large for the
    // console, so this one is deliberately richer than the dry-run listing.
    // `--jq` flattens the connections and keeps only the OPEN blockers, so each
    // issue reaches the prompt with a plain `blockedBy: [<issue numbers>]`.
    plannerListCommand:
      "gh api graphql -F owner='{owner}' -F name='{repo}' -f query='" +
      issueListQuery(
        "number title body labels(first:20){nodes{name}} " +
        "comments(first:100){nodes{body}}",
      ) +
      "' --jq '[.data.repository.issues.nodes[] | {number, title, body, " +
      "labels: [.labels.nodes[].name], comments: [.comments.nodes[].body], " +
      `blockedBy: [.blockedBy.nodes[] | select(.state == "OPEN") | .number]}]'`,

    closeComment: "Completed by Sandcastle",
  },

  git: {
    // Gitflow (ADR-0007): rounds are planned, merged and gated on `develop`.
    // Launch the orchestrator from it; `main` only ever receives release and
    // hotfix merges.
    integrationBranch: "develop",
    // AGENTS.md reserves this prefix for agent branches.
    branchPrefix: "sandcastle/issue-",
  },

  // Run in order on the merged HEAD, stopping at the first failure. Each gate
  // is spawned WITHOUT a shell, so `file` + `args`, never a command string.
  //
  // Scope note: the build is `-warnaserror` because the solution treats
  // warnings as errors, and the test gate is the whole solution — including the
  // Testcontainers-backed integration suites, which is why it needs Docker on
  // the host and gets the long timeout. The client apps' browser suites are
  // deliberately NOT here: they take tens of minutes, branch reviewers already
  // run the scoped ones, and CI runs them in full on the eventual push.
  gates: [
    {
      name: "backend build",
      file: "dotnet",
      args: ["build", "src/Boilerplate.slnx", "-warnaserror"],
      timeoutMs: 20 * 60_000,
      parser: "dotnetBuild",
    },
    {
      name: "backend full suite",
      file: "dotnet",
      args: ["test", "src/Boilerplate.slnx"],
      timeoutMs: 45 * 60_000,
      parser: "dotnetTest",
      requiresDocker: true,
    },
  ],

  limits: {
    // Raise for a large backlog; lower for a quick smoke-test run.
    maxIterations: 40,

    // A RAM budget, not a throughput dial. Each agent may run a full Release
    // build with analyzers plus a test suite: budget ~3-4 GB of Docker RAM per
    // agent, leave ~2 GB for sidecars plus ~2-3 GB VM headroom, and size from
    // the machine you are actually on — `docker info | grep -i 'total memory'`,
    // then (total − 2 − 3) / ~4. Over-provisioning OOM-kills implementers
    // mid-gate (and collaterally kills their compilers, spuriously reddening
    // the survivors' gates); under-provisioning only costs wall-clock.
    // Per-machine override: MAX_CONCURRENT_AGENTS in .sandcastle/.env.
    maxConcurrentAgents: 3,

    // Deliberately deeper than the concurrency cap: a queue no deeper than the
    // cap is empty by construction, so a pipeline that finishes early idles its
    // slot until the slowest sibling ends. A deeper queue turns that dead slot
    // into the next issue at identical RAM, and amortises the round's serial
    // tail (planner + merger + the host-side gates, during which EVERY slot
    // idles) over more issues. The ceiling is merge risk, not memory: every
    // queued issue is one more branch the single merger must land in one pass.
    plannerQueueDepth: 10,

    // The timer resets only on AGENT output, and a foreground gate emits none
    // until its tool call returns — so this must stay ABOVE the sandbox's
    // BASH_MAX_TIMEOUT_MS (one hour, below), or an implementer is killed
    // mid-suite with its whole tree uncommitted.
    idleTimeoutSeconds: 3900,

    // HEALING IS OFF BY DEFAULT (ADR-0006). The healer runs on the HOST with
    // permission prompts bypassed, because the sandboxes have no Docker and
    // could never reproduce a container-backed failure. That is a real trust
    // decision and belongs to the operator: turn it on for a single run with
    // SANDCASTLE_HEAL_ATTEMPTS=2 (each attempt costs one healer session plus a
    // full gate re-run).
    healAttempts: 0,
  },

  // Which model each phase runs on, and at what reasoning effort. An unset
  // effort is the CLI default (high) — the level for long-horizon agentic work
  // with the spec given up front, which is what every phase here is. Don't
  // reach for `effort: "low"` on the implementer: low effort means fewer,
  // terser tool calls, the wrong shape for an autonomous run. There is no turn
  // or budget cap, so cost is controlled by the choices here and by the
  // edit-tool rule in implement-prompt.md.
  models: {
    // Dependency reasoning across the whole open backlog.
    planner: { model: CLAUDE.opus5Long },
    // Long-horizon agentic coding, the bulk of the work. Opus at 200K with
    // auto-compaction is half Fable's per-token price; the pipeline's quality
    // comes from the tickets' precision, the reviewer and the merge gate, not
    // from the implementer's model. Switch to `CLAUDE.fable51Long` when usage
    // is not the constraint: it reaches a commit in about half the turns.
    implementer: { model: CLAUDE.opus5 },
    // The quality net: one pass over the diff, and the layer that actually
    // catches defects. 1M context so a big diff fits.
    reviewer: { model: CLAUDE.fable51Long },
    // Conflict resolution plus a full gate run.
    merger: { model: CLAUDE.opus5Long },
    // Hard debugging of a red merged HEAD on the host — the most capable
    // model; a handful of failing tests and one seam fit easily.
    healer: { model: CLAUDE.fable51Long },
  },

  sandbox: {
    // Shared host-side caches, bind-mounted into every sandbox so restores and
    // installs are warm after the first round. All of these tolerate concurrent
    // readers and writers.
    cacheRoot: "~/.sandcastle-cache",
    caches: [
      { hostDir: "nuget", sandboxPath: "/home/agent/.nuget/packages" },
      { hostDir: "npm", sandboxPath: "/home/agent/.npm" },
      { hostDir: "pnpm-store", sandboxPath: "/home/agent/.pnpm-store" },
      { hostDir: "pnpm-cache", sandboxPath: "/home/agent/.cache/pnpm" },
      { hostDir: "ms-playwright", sandboxPath: "/home/agent/.cache/ms-playwright" },
    ],

    env: {
      DOTNET_SKIP_FIRST_TIME_EXPERIENCE: "true",
      DOTNET_CLI_TELEMETRY_OPTOUT: "true",
      DOTNET_NOLOGO: "true",
      // Pin the package cache to the shared mount so restores stay warm even
      // if an agent redirects HOME.
      NUGET_PACKAGES: "/home/agent/.nuget/packages",
      // Claude Code caps a single Bash tool call at 10 minutes by default
      // (BASH_MAX_TIMEOUT_MS=600000). A longer gate is moved to the background
      // on timeout, and an agent that then "waits for the result" by ending its
      // turn ENDS THE RUN in --print mode: the sandbox is torn down mid-suite
      // and the merge lands ungated. Raise both caps to one hour so every gate
      // runs in the foreground, and keep `idleTimeoutSeconds` above this — a
      // foreground tool call is silent on the agent stream for its duration.
      BASH_DEFAULT_TIMEOUT_MS: "3600000",
      BASH_MAX_TIMEOUT_MS: "3600000",
      // Cap the browser suites at 2 workers per sandbox. All sandboxes share
      // ONE Docker VM, and the configs' local default is half the cores PER
      // process — three concurrent suites then oversubscribe the VM, miss click
      // timeouts en masse, and turn a 25-minute suite into an hour of re-runs.
      PLAYWRIGHT_WORKERS: "2",
    },

    // The healer runs on the host, where the shared mounts do not apply.
    hostEnv: {
      DOTNET_SKIP_FIRST_TIME_EXPERIENCE: "true",
      DOTNET_CLI_TELEMETRY_OPTOUT: "true",
      DOTNET_NOLOGO: "true",
      BASH_DEFAULT_TIMEOUT_MS: "3600000",
      BASH_MAX_TIMEOUT_MS: "3600000",
    },

    // One orchestrator per machine: an exclusive loopback bind, atomic in the
    // OS, held for the whole run and self-releasing on any death.
    sentinelPort: 17032,
  },

  prompts: {
    plan: "./.sandcastle/plan-prompt.md",
    implement: "./.sandcastle/implement-prompt.md",
    review: "./.sandcastle/review-prompt.md",
    merge: "./.sandcastle/merge-prompt.md",
    heal: "./.sandcastle/heal-prompt.md",
  },
});
