---
status: accepted
---
# The sandcastle agent pipeline is generic code behind one typed config

`.sandcastle/` orchestrates plan → implement → review → merge → post-merge gate → close on top of the `@ai-hero/sandcastle` package. All logic modules (agent pool, issue pipeline, branch guard, gate heal, post-merge gate, close ledger, merger dirt) are pure, dependency-injected and covered by `node:test` suites that need no Docker, models or network. Everything repo-specific — project name, issue label and query, branch prefix, integration branch (`develop`, ADR-0007), gate commands and their log parsers, models, limits — lives in a single typed `sandcastle.config.mts`, and prompts receive gate commands as a generated `{{GATE_COMMANDS}}` argument instead of hard-coding them. Renaming the project or swapping the stack touches the config, not the pipeline.

Decisions that are deliberately fail-closed: a gate result is RED only when the gate delivered a verdict, and UNVERIFIED (Docker down, timeout, interrupt) is never healed or merged; issues are closed only when git proves the branch tip is an ancestor of the integration branch, recorded in a durable ledger; gates run in their own process group so a timeout kills the whole test tree.

Post-merge healing runs an agent on the host with permission prompts bypassed, because sandboxes have no Docker for integration tests. That is a real trust decision, so healing is **off by default** (`SANDCASTLE_HEAL_ATTEMPTS=0`) and opt-in per machine.

The sandcastle tests run in CI; upstream of this decision they ran only on demand.
