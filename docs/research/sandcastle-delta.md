# Sandcastle pipeline: what the owner's newer copy has that this repo lacks

Research for [#90](https://github.com/BeshoyHindy/dotnet-saas-boilerplate/issues/90), part of map #87.

**Question.** This repo's `.sandcastle/` pipeline was ported from a private copy the owner still
runs and keeps changing ([ADR-0006](../adr/0006-sandcastle-pipeline-behind-one-config.md)). Which of
that copy's later changes are general-purpose behaviours this template lacks, what would each cost to
port behind `sandcastle.config.mts`, and what does this repo have that the other copy lacks?

**Sources.** Everything under this repo's `.sandcastle/` and `sandcastle.config.mts`; the pinned
orchestration library `@ai-hero/sandcastle` 0.12.0, read in `node_modules/@ai-hero/sandcastle/dist/`;
and the owner's newer copy (its pipeline directory, its git history and its commit messages), read
locally on 2026-09-26. The public-repo rule applies: this file describes behaviours and this repo's
code only. It gives no paths, values, excerpts or names from the other copy.

## Summary

| Difference | General-purpose? | Port cost | Recommendation |
|---|---|---|---|
| Wait out a usage limit and re-run the phase | Yes | Medium: one pure module plus wiring at 5 call sites | **Adopt** |
| Start a run while a limit is already spent | Yes (part of the above) | Small | **Adopt**, together with the wait |
| Implementer continues from a cut-off attempt's uncommitted work | Yes (needed by the wait) | Prompt-only | **Adopt**, together with the wait |
| Optional fallback Claude account, with return to the main one | Yes, opt-in | Small on top of the wait | **Adopt**, off by default |
| Switch accounts by command prefix, not agent env | Needed by the fallback | Included above | **Adopt**, together with the fallback |
| Round cap overridable from env, higher default | Yes | Tiny | **Adopt** |
| Planner queue depth overridable from env | Yes, minor | Tiny | Optional |
| Reviewer: no style-only commits; check docs for contradictions | Mostly (a cost choice) | Prompt-only | Optional |
| Automatic wave release of `ready-for-agent` | Generic code, tuned defaults | Small | **Defer** |
| Per-phase model/effort choices | No (tuned to that workload) | n/a | **Skip** |
| Healing on by default | No (ADR-0006 says otherwise) | n/a | **Skip** |
| Per-phase `env` field on the model table | Unused there | n/a | **Skip** |

The shared logic modules have **not drifted in behaviour**. The newer copy's changes are all additive:
two new modules, their wiring in the orchestrator, and prompt and config tuning.

## Shared files: what the diff actually shows

A file-by-file diff of every file both pipelines have:

- **No behavioural drift.** `agent-pool.mts`, `branch-guard.mts`, `close-ledger.mts`,
  `issue-pipeline.mts`, `merger-dirt.mts` and their tests. `branch-guard` is byte-identical. The rest
  differ only in comments (this repo dropped incident dates and issue numbers when it ported them) and
  in test fixture data (issue ids, branch names).
- **Diverged, and this repo is ahead.** `post-merge-gate.mts` and `gate-heal.mts`. The other copy
  still hard-codes one post-merge gate and a single log reader shaped for one test runner. This repo
  takes an ordered gate list from `config.gates`, reads each log with that gate's own parser
  (`.sandcastle/log-parsers.mts`), and hands the healer the failed gate's verdict
  (`gate-heal.mts`, `failuresOf: (red: RedGateResult)`), so the healer is briefed in the shape of the
  gate that actually failed.
- **Stack content only.** `Dockerfile`, `CODING_STANDARDS.md` and the bulk of the five prompts
  differ by that project's stack: its solution and client names, its package-manager policy, its
  browser-suite rules. None of it is portable, and this repo already carries equivalents (gate
  commands arrive through `{{GATE_COMMANDS}}`, see `.sandcastle/config.mts` `formatGateCommands`).
- **Portable prompt deltas.** Two: the implementer's resume block (see
  [below](#3-the-implementer-continues-from-a-cut-off-attempt)) and the reviewer's scope rules (see
  [below](#7-reviewer-scope-no-style-only-commits-check-docs)). Everything else in the prompts
  already matches. This repo has the acceptance-criteria walk, the "never run `git worktree` in a
  sandbox" rule, the "gate the merge only when it needed real work" rule, the healer's
  hypothesis-ranking discipline and the exclusion of generated artifacts from the reviewer's diff
  (`.sandcastle/implement-prompt.md`, `review-prompt.md`, `merge-prompt.md`, `heal-prompt.md`,
  `CODING_STANDARDS.md`).
- **Orchestrator.** `main.mts` is where the real differences are, covered one by one below.

## The differences

### 1. Wait out a Claude usage limit and re-run the phase

**What it does.** A Claude subscription runs out every few hours. When it does, `claude -p` exits
non-zero and the library reports a plain agent error, like any other failure. What happens next
depends on the phase. In this repo's `main.mts` today, a planner failure crashes the process, a
merger or healer failure exits the run, and every queued implementer fails in turn. An unattended
overnight run therefore stops at the first limit.

The newer copy runs every Claude phase (planner, implementer, reviewer, merger, healer) through a
small waiter:

- **Recognise the limit by probing, not by matching text.** When a phase throws, the host runs a
  one-line `claude -p` probe against that phase's model: no tools, no MCP servers, no session
  persistence, one turn, run from a temporary directory so no repo instructions load. If the model
  answers, the failure was real and the original error is re-thrown unchanged. If it does not, the
  account cannot reach the model (a usage limit or an outage), so the phase waits. Text matching was
  rejected because the wording moves between Claude Code releases, and because the library builds its
  error from stderr first, where unrelated warnings land, so the limit text is often missing from the
  error.
- **Accept both probe output shapes.** Newer Claude Code releases print `--output-format json` as an
  array of stream events, older ones as a bare result object. Only a `result` event with
  `is_error: false` counts as "answered". Anything unparseable counts as "not answering".
- **One shared wait per model.** When three implementers hit the limit together, one probe loop runs
  and all three resume when it clears. Without this, each phase would poll on its own.
- **A budget per call, starting at the first limit hit.** An implementer that worked for hours before
  the limit still gets the whole wait. A phase that keeps failing cannot loop forever, because
  retries share the same budget. When the budget runs out, the original error is re-thrown, which
  covers a weekly limit or a dead token. A budget of 0 turns the wait off and spends no probe.
- **A startup self-check that fails safe.** If the host `claude` cannot run the probe at all (no CLI,
  a release without one of the flags, bad credentials), every probe would fail and every *real* phase
  failure would turn into a long wait. So the orchestrator runs the real probe once at startup. If
  the probe does not answer, the wait is switched off for that run and the run behaves as it does
  today.

**General-purpose?** Yes. Nothing in it is tied to a stack or a workload. It applies to anyone who
runs this pipeline on a Claude subscription, which is what `.sandcastle/.env.example` sets up by
default.

**Port cost behind `sandcastle.config.mts`.**

- A new pure module, `.sandcastle/usage-limit.mts`, and its test. Both port nearly verbatim; strip
  the incident prose, as this repo did for the other modules. It already has the shape this repo's
  modules use: injected probe, sleep, clock and log, and no Docker, model or network in its tests.
- `config.mts`: a `limits` field for the poll interval and the wait budget, plus env overrides in
  `resolveLimits` (for example `SANDCASTLE_USAGE_POLL_MINUTES` and
  `SANDCASTLE_USAGE_MAX_WAIT_HOURS`) that throw on a malformed value, matching the existing pattern.
  Add cases to `config.test.mts`.
- A config field naming the cheap model the startup probe uses, so no model id is hard-coded at the
  call site. This follows the rule in `sandcastle.config.mts` against hard-coding an id at a call
  site.
- `main.mts`: the host probe (`execFile("claude", …)`), the startup check, and `usage.run(label,
  model, fn)` around the five phase call sites (planner at `sandcastle.run`, implementer and
  reviewer at `sandbox.run`, merger and healer at `sandcastle.run`).
- `dry-run.mts`: report whether the wait is on and with what budget, with a `dry-run.test.mts` case.
- `.env.example`: the new keys, pre-filled with the config defaults as the file already does.
- A default for this repo: poll every few minutes, with a budget comfortably longer than one
  five-hour usage window.

Roughly one new module of about 280 lines, about 100 lines in `main.mts`, and small edits to
`config.mts`, `dry-run.mts` and `.env.example`.

**Tests it carries.** 22 `node:test` cases in total, covering this item and items 2 and 4. The wait
cases: success never probes; a real failure is re-thrown with no wait; wait-then-resume; give up at
the budget; the budget is per call, not per retry; concurrent phases share one probe loop; the
failing phase's own model is probed; the budget starts at the first limit hit; a budget of 0 means
no probe. The parser cases cover the array and bare-object output shapes. This repo's CI would pick
the file up with no change, because `pnpm test:sandcastle` globs `.sandcastle/*.test.mts`
(`package.json`) and `.github/workflows/sandcastle.yml` runs on `.sandcastle/**`.

### 2. Start a run while a limit is already spent

**What it does.** The first version of the startup self-check treated every non-answer as "broken
CLI" and turned the wait off. But the natural moment to relaunch a stopped run is right after a limit
hit, while the quota is still spent. A run started then failed every phase fast and stranded
finished branches. The fix: at startup only, a probe whose result is an error that names a limit
counts as "CLI works, quota spent". The wait stays on, and the first phase simply waits for the
reset. Any other non-answer still turns the wait off. Text is matched only for this one startup
decision. The mid-run wait still trusts the probe alone.

**General-purpose?** Yes. It is the same feature as item 1, closing a hole in its fail-safe.

**Port cost.** Included in item 1: one extra parser function in the module, and one branch in the
startup check.

**Tests.** Included in the 22: a limit refusal counts as a spent quota, while a login error or
garbage does not.

### 3. The implementer continues from a cut-off attempt

**What it does.** When the wait re-runs an implementer, it re-runs inside the *same* issue sandbox,
so the cut-off attempt's uncommitted edits are still in the worktree. The newer copy's implementer
prompt shows `git status --short` in its context. When that list is non-empty, the prompt tells the
agent that an earlier attempt was cut off, to read the diff, keep what is sound and finish from
there, instead of starting over.

**General-purpose?** Yes. It only matters once item 1 exists.

**Port cost.** Prompt-only: a short block in `.sandcastle/implement-prompt.md`, using the library's
existing `` !`…` `` shell-block substitution, which the prompt already uses for `git log`.

**Tests.** None. It is prompt text.

### 4. Optional fallback Claude account, with return to the main one

**What it does.** The run is given accounts in priority order: the main one, then an optional
fallback.

- When a phase's account is out, the other accounts are probed in order, and the phase re-runs at
  once on the first one that answers. New phases start there too.
- While the run is on the fallback, the main account is re-probed at most once per poll interval,
  shared across concurrent phases. New phases move back as soon as it answers. Phases already
  running stay where they are.
- When no account answers, the run waits and probes every account. The main account wins a tie.
- With one account, this is exactly item 1.
- The fallback is **off by default**, with a separate on/off switch, so a token can stay in `.env`
  without being spent.
- At startup, the fallback token is probed and dropped with a warning if it is broken (as opposed to
  merely limited), so a typo never costs a round.
- The run refuses to start with the fallback enabled while `ANTHROPIC_API_KEY` is set, because Claude
  Code prefers the API key and would ignore the swapped token.

**General-purpose?** Yes, for anyone with a second subscription. It is opt-in, so it costs nothing
when unused.

**Port cost.** Small on top of item 1: the module already takes an `accounts` list. Add a config or
env switch (for example `SANDCASTLE_FALLBACK_ACCOUNT=on|off` plus
`CLAUDE_CODE_OAUTH_TOKEN_FALLBACK`), validate it in `resolveLimits` or a sibling resolver, show it
in `--dry-run`, and document it in `.env.example`.

**Caveats to carry into the port.**

- **The fallback token lands in every sandbox.** `.sandcastle/.env.example` currently says "nothing
  here is ever read inside a sandbox", and that understates what happens. The library's `resolveEnv`
  (`node_modules/@ai-hero/sandcastle/dist/index.js`) exports every key listed in
  `.sandcastle/.env`, with its value from the file or the host env, into each sandbox's environment.
  The main token already travels this way. A fallback token would too, which is also what makes the
  switch work (item 5). The `.env.example` wording is worth correcting either way.
- **The fallback token must be listed in `.sandcastle/.env` itself.** `resolveEnv` forwards only keys
  named in that file, so a token exported from the shell alone never reaches a sandbox.
- **It has not been run end-to-end in a container.** The newer copy's own history records the switch
  as verified through `sh -c` on the host only, with Docker not running at the time. The library code
  supports it (item 5), but the port should include one live check in a container.

**Tests.** 7 of the 22 cover it:

- a limit on the main account re-runs the phase at once on the fallback
- new phases start on the fallback
- new phases return to the main account, checked at most once per poll interval
- a real failure on the fallback is re-thrown, not bounced back to the main account
- with both accounts out, the run waits and then resumes on whichever answers first
- with both accounts out past the budget, the original error is re-thrown
- the phase is handed the account it runs on

Two more cover item 5.

### 5. Switch accounts by command prefix, not agent env

**What it does.** A phase runs on the fallback by prefixing its `claude` command with
`CLAUDE_CODE_OAUTH_TOKEN="$CLAUDE_CODE_OAUTH_TOKEN_FALLBACK"`. It does not pass the token through
the agent's env. The newer copy first tried the env route and found it silently ran "fallback"
phases on the limited main token.

The pinned library confirms why:

- `createSandbox` merges env with `agentProviderEnv: {}`, so the implementer and reviewer, which share
  one sandbox per issue started before any account is picked, never see an agent-level env
  (`dist/index.js`, the `createSandbox` path next to `resolveEnv`).
- The docker sandbox runs every command as `docker exec … sh -c <command>`
  (`dist/chunk-CP3TYXZA.js`), and `noSandbox`, used by the healer, runs it through `sh -c`
  (`dist/chunk-62WN33RK.js`). A `VAR="$OTHER" claude …` prefix therefore applies to that one
  command, and `$OTHER` expands inside the sandbox from the forwarded env.
- `claudeCode().buildPrintCommand` returns `{ command, stdin }` (`dist/index.js`), so a wrapper can
  prefix `command` and leave the prompt on stdin.

The secret never appears on a command line. Only the variable name does.

**General-purpose?** Yes. It is the only way to switch accounts mid-run with this library version.

**Port cost.** Included in item 4: a roughly 15-line `onAccount(provider, account)` wrapper applied
around `agentFor(...)` at the five call sites.

**Tests.** 2: the main account's command is left untouched; the fallback's prefix carries the
variable name, never the secret, and the prompt stays on stdin.

### 6. Round cap overridable from env, higher default

**What it does.** The newer copy reads the round cap from `SANDCASTLE_MAX_ITERATIONS` and raised its
built-in default well above this repo's 40. The loop already exits as soon as the planner finds no
unblocked issue, so a high cap costs nothing on a small backlog. It only stops a long unattended run
from ending early, which matters more once item 1 lets a run span several usage windows.

**General-purpose?** Yes. The default value is this repo's call; the override is plumbing.

**Port cost.** Tiny. One more `readInt("SANDCASTLE_MAX_ITERATIONS", limits.maxIterations, 1)` in
`resolveLimits` (`.sandcastle/config.mts`), a `config.test.mts` case, an `.env.example` line, and
optionally a higher `limits.maxIterations` in `sandcastle.config.mts`.

**Tests.** None in the newer copy. This repo's port would add the `config.test.mts` case, as it does
for `MAX_CONCURRENT_AGENTS`.

### 7. Reviewer scope: no style-only commits; check docs

**What it does.** The newer copy's review prompt makes two changes:

- It declares style-only refinements out of scope: renames, reflowed comments, extracted helpers,
  collapsed ternaries, memoisation with no behaviour change. Each one costs a gate re-run for no
  behavioural gain, so the reviewer mentions them in its report instead of committing them.
- It adds a check for a rules file, README or doc comment that the branch left contradicting the
  new behaviour.

This repo's `.sandcastle/review-prompt.md` instead has a **Clarity** section that invites exactly
those refinements.

**General-purpose?** The docs check, yes. The style rule is a cost-versus-polish choice. It is
reasonable for any unattended run, but it is a judgment call rather than a fix.

**Port cost.** Prompt-only.

**Tests.** None.

### 8. Automatic wave release (deferred)

**What it does.** Before each planning round, when fewer than a threshold of issues are
`ready-for-agent`, the host labels the next unreleased issues from label-prefixed "waves" as
`ready-for-agent`: lowest wave first, then lowest issue number. It tops up to a target that is
capped under the planner's read limit, so no ready issue is hidden from the planner. Issues in a
held triage state (`ready-for-human`, `needs-info`, `needs-triage`, `wontfix`, or already ready) are
never released. A GitHub failure is logged, and the round plans with what is already ready. The
feature can be turned off.

**General-purpose?** The selection code is generic: a 60-line pure function. Its defaults (the label
prefix and the thresholds) are tuned to one large backlog.

**Why defer.** It changes this repo's triage contract. `docs/agents/triage-labels.md` and
`AGENTS.md` say only issues a human has judged fully specified reach an agent. Auto-release moves the
last step, applying `ready-for-agent`, from a human to the host. It is still a human who chose the
wave, but the contract text would need to change first. A freshly scaffolded product also has no
backlog to meter. Revisit it when a product runs a wave-labelled backlog.

**Port cost, if taken.**

- An optional `issues.release` block in the config (`labelPrefix`, `below`, `upTo`), off when absent.
- The module and its 7 tests, nearly verbatim.
- One host call before planning.
- The planner's read cap here lives in the GraphQL query in `sandcastle.config.mts`
  (`issues(first:100, …)`), so the port must derive the cap from config, not from a constant.

### 9. Per-phase model and effort choices (skip)

The newer copy re-tuned which model and effort level each phase uses, from its own session-cost
measurements. Those choices follow its workload and its usage headroom, not anything structural.
This repo already lets an operator try any combination per machine or per run, with no code edit:
`SANDCASTLE_<PHASE>_MODEL` and `SANDCASTLE_<PHASE>_EFFORT`, validated by `resolveModels` in
`.sandcastle/config.mts` and shown by `--dry-run`. The newer copy has no such overrides. Nothing to
port.

### 10. Healing on by default (skip)

The newer copy hands a red post-merge gate to the healer by default. This repo keeps healing off
(`limits.healAttempts: 0`), because the healer runs on the host with permission prompts bypassed.
That is a recorded trust decision ([ADR-0006](../adr/0006-sandcastle-pipeline-behind-one-config.md)),
and map #87's notes say a recorded decision wins.

### 11. Per-phase `env` on the model table (skip)

The newer copy's model table accepts an optional `env` per phase, merged into the agent's env. No
phase sets it. And as item 5 shows, agent env never reaches the shared implementer/reviewer sandbox
in this library version, so it would mislead as much as help.

## What this repo has that the newer copy lacks

- **One typed config** (`sandcastle.config.mts` via `defineConfig`). The other copy keeps its
  project name, gates, models, limits, caches, sentinel port and issue query inline in `main.mts` and
  its prompts. This repo also has resolvers that throw on a malformed override: `resolveLimits` and
  `resolveModels` in `.sandcastle/config.mts`, with 23 tests in `config.test.mts`.
- **`--dry-run`**, which resolves the config, prints the gate commands the prompts will receive and
  the issues the planner would see, and starts nothing (`.sandcastle/dry-run.mts`, 23 tests).
- **A post-merge gate list with per-gate log parsers** (`.sandcastle/log-parsers.mts`, 13 tests), and
  a healer briefed by the failed gate's own parser. The other copy has one hard-coded gate and one
  log reader.
- **Native GitHub blocking edges fed to the planner** through GraphQL `blockedBy`, with closed
  blockers dropped (`sandcastle.config.mts`, `issueListQuery`). The other copy's planner lists issues
  with `gh issue list` and relies on "Blocked by" text in issue bodies.
- **Per-phase model and effort overrides from `.sandcastle/.env`**, pre-filled in `.env.example`.
- **Gate commands and the project name injected into prompts** (`{{GATE_COMMANDS}}`,
  `{{PROJECT_NAME}}`) rather than written into them.
- **Healing off by default**, with the reason recorded (ADR-0006).
- **Pipeline tests in CI** (`.github/workflows/sandcastle.yml`), and a template-smoke matrix that
  scaffolds with and without the pipeline (`.github/workflows/template-smoke.yml`).
- **More tests:** 158 `node:test` cases here against 133 there, before any port.

## Recommendation

**Adopt, as one slice.** Items 1–5 are one feature:

- the usage-limit wait, with the probe-not-text rule, a shared wait per model, a per-call budget
  from the first hit and the fail-safe startup probe
- starting a run inside a limit
- the implementer's resume block
- the fallback account, off by default, switched by command prefix

Carry the three caveats from item 4 into the port: the token reaches every sandbox, it must be
listed in `.sandcastle/.env`, and the switch needs one live check in a container. Correct the
`.env.example` line that says nothing in it is read inside a sandbox.

**Adopt, small.** Item 6: `SANDCASTLE_MAX_ITERATIONS` through `resolveLimits`, and a higher
`limits.maxIterations`.

**Optional.** A `PLANNER_QUEUE_DEPTH` env override, following the same pattern. This repo already
floors the depth at the concurrency cap. And item 7's reviewer prompt deltas: the docs-contradiction
check is the stronger half.

**Defer.** Item 8, wave release, until the triage contract says the host may apply
`ready-for-agent`, and a product has a backlog to meter.

**Skip.** Item 9 (per-phase model and effort values), item 10 (healing on by default, since
ADR-0006 wins), item 11 (the per-phase `env` field), and all stack-specific Dockerfile, standards
and prompt content.

## Open question

`sandcastle.config.mts` runs the reviewer on the explicit `[1m]` 1M-context selector and explains
why. Before the next model change, check Claude Code's current documentation for whether that
selector still means anything for the models in use, or whether they now have the full window
natively.
