// The shape of the one repo-specific file — `sandcastle.config.mts` at the
// repo root — plus the pure helpers that derive things from it.
//
// Nothing in `.sandcastle/` may hard-code a project name, a branch, an issue
// label, a solution file or a gate command: every one of those lives in the
// root config, and every module here receives what it needs as an argument.
// Only `main.mts` imports the root config; the logic modules never do, which
// is what keeps them testable with neutral fixtures and no Docker.
//
// This file deliberately holds NO values — only types, defaults-free helpers
// and the `defineConfig` identity that gives the root file its type checking.

import type { LogParserName } from "./log-parsers.mts";

/** The agent phases, in the order a round runs them. */
export type PhaseName =
  | "planner"
  | "implementer"
  | "reviewer"
  | "merger"
  | "healer";

export interface PhaseModel {
  /** A model id the agent CLI understands, e.g. `claude-opus-5`. */
  readonly model: string;
  /**
   * Reasoning effort. Unset means the CLI default (high) — the level for
   * long-horizon agentic work with the spec given up front, which is what
   * every phase here is.
   */
  readonly effort?: "low" | "medium" | "high";
}

export interface GateConfig {
  /** Human name, used in logs and in the agent briefings. */
  readonly name: string;
  /** Executable, run WITHOUT a shell — no pipes, no globs, no `&&`. */
  readonly file: string;
  readonly args: readonly string[];
  readonly timeoutMs: number;
  /** Which log parser turns this gate's log into an excerpt / failure blocks. */
  readonly parser: LogParserName;
  /**
   * The gate cannot run without a reachable Docker daemon, so a failure is
   * only evidence about HEAD if Docker is still up when it fails.
   */
  readonly requiresDocker?: boolean;
}

export interface IssuesConfig {
  /** The triage label that marks an issue ready for an autonomous agent. */
  readonly label: string;
  /**
   * `gh` arguments (without the leading `gh`) listing the open, agent-ready
   * issues as JSON with at least `number` and `title`. Used by the host for
   * the dry run; run WITHOUT a shell, so no `--jq` pipelines here.
   */
  readonly listArgs: readonly string[];
  /**
   * The richer one-line shell command the PLANNER runs inside its prompt
   * (bodies and comments included, which is what dependency reasoning needs).
   * Substituted into the prompt's shell block, so it may use pipes and `--jq`.
   */
  readonly plannerListCommand: string;
  /** The comment left on an issue the host closes after its work landed. */
  readonly closeComment: string;
}

export interface GitConfig {
  /** The branch rounds are merged into and launched from. */
  readonly integrationBranch: string;
  /** Issue branches are `${branchPrefix}${issueId}` — no slug, no suffix. */
  readonly branchPrefix: string;
}

export interface LimitsConfig {
  /** Maximum plan → execute → merge cycles before the run stops. */
  readonly maxIterations: number;
  /**
   * Hard cap on simultaneously running issue sandboxes. A RAM budget, not a
   * throughput dial. Overridden per machine by `MAX_CONCURRENT_AGENTS`.
   */
  readonly maxConcurrentAgents: number;
  /**
   * How many issues the planner queues per round — deliberately MORE than can
   * run at once, so a pipeline that finishes early starts the next issue
   * instead of idling its slot. Clamped up to `maxConcurrentAgents`.
   */
  readonly plannerQueueDepth: number;
  /**
   * Idle timeout for code-writing/validating phases. The timer resets only on
   * AGENT output and a foreground gate emits none until its tool call returns,
   * so this must stay ABOVE the sandbox's `BASH_MAX_TIMEOUT_MS`.
   */
  readonly idleTimeoutSeconds: number;
  /**
   * How many times a RED post-merge gate is handed to a healer agent before
   * the run stops as red. 0 means the first red verdict is final. Overridden
   * per run by `SANDCASTLE_HEAL_ATTEMPTS`.
   */
  readonly healAttempts: number;
}

export interface CacheMount {
  /** Directory under the cache root on the host. */
  readonly hostDir: string;
  /** Where it is mounted inside the sandbox. */
  readonly sandboxPath: string;
}

export interface SandboxConfig {
  /**
   * Host directory holding the shared caches bind-mounted into every sandbox.
   * `~` expands to the home directory.
   */
  readonly cacheRoot: string;
  readonly caches: readonly CacheMount[];
  /** Environment for every sandboxed phase. */
  readonly env: Readonly<Record<string, string>>;
  /**
   * Environment for the phase that runs ON THE HOST (the healer). Kept apart
   * because the host has no bind mounts to point at.
   */
  readonly hostEnv: Readonly<Record<string, string>>;
  /**
   * Loopback port held for the whole run so a second orchestrator on the same
   * machine refuses to start. Two orchestrators race each other on the same
   * issue branches and merge-to-HEAD syncs.
   */
  readonly sentinelPort: number;
}

export interface PromptsConfig {
  readonly plan: string;
  readonly implement: string;
  readonly review: string;
  readonly merge: string;
  readonly heal: string;
}

export interface SandcastleConfig {
  readonly project: { readonly name: string };
  readonly issues: IssuesConfig;
  readonly git: GitConfig;
  /** Run in order on the merged HEAD; the first failure stops the rest. */
  readonly gates: readonly GateConfig[];
  readonly limits: LimitsConfig;
  readonly models: Readonly<Record<PhaseName, PhaseModel>>;
  readonly sandbox: SandboxConfig;
  readonly prompts: PromptsConfig;
}

/**
 * Identity function that gives the root config file its type checking and
 * editor completion. `export default defineConfig({ … })`.
 */
export function defineConfig(config: SandcastleConfig): SandcastleConfig {
  return config;
}

// ---------------------------------------------------------------------------
// Derived values — pure, so they are tested without a config file on disk.
// ---------------------------------------------------------------------------

/** Quote an argument only when a shell would otherwise mangle it. */
function shellQuote(arg: string): string {
  return /^[A-Za-z0-9_@%+=:,./-]+$/.test(arg) ? arg : `'${arg.replaceAll("'", `'\\''`)}'`;
}

/** One gate as the command line a human (or an agent) would type. */
export function gateCommand(gate: GateConfig): string {
  return [gate.file, ...gate.args].map(shellQuote).join(" ");
}

/** `a + b` — the combined gate name for a briefing headline. */
export function gateNames(gates: readonly GateConfig[]): string {
  return gates.map((gate) => gate.name).join(" + ");
}

/** `a && b` — every gate as one copy-pasteable command. */
export function joinGateCommands(gates: readonly GateConfig[]): string {
  return gates.map(gateCommand).join(" && ");
}

/**
 * The `{{GATE_COMMANDS}}` prompt argument: the gates as a markdown list, each
 * with its name and an indented, copy-pasteable command.
 *
 * This is the ONLY way a prompt learns a gate command. Prompts that hard-code
 * one drift the moment the solution is renamed or a gate is added — the drift
 * this config exists to make impossible.
 */
export function formatGateCommands(gates: readonly GateConfig[]): string {
  if (gates.length === 0) {
    return "(no gates are configured for this repository)";
  }
  return gates
    .map((gate) => `- **${gate.name}**\n\n      ${gateCommand(gate)}`)
    .join("\n\n");
}

export interface ResolvedLimits extends LimitsConfig {
  /** `plannerQueueDepth` after the concurrency-cap floor is applied. */
  readonly plannerQueueDepth: number;
}

/**
 * Apply the per-machine / per-run environment overrides to the configured
 * limits, and clamp the planner queue so a low value can never starve the pool
 * it feeds.
 *
 * THROWS on a malformed override rather than silently falling back: a typo in
 * `MAX_CONCURRENT_AGENTS` that quietly restores the default would OOM-kill
 * implementers mid-gate on a machine sized for fewer.
 */
export function resolveLimits(
  limits: LimitsConfig,
  env: Readonly<Record<string, string | undefined>>,
): ResolvedLimits {
  const readInt = (name: string, fallback: number, min: number): number => {
    const raw = env[name]?.trim();
    if (raw === undefined || raw === "") {
      return fallback;
    }
    const parsed = Number(raw);
    if (!Number.isInteger(parsed) || parsed < min) {
      throw new Error(
        `${name} must be an integer >= ${min}, got ${JSON.stringify(raw)}`,
      );
    }
    return parsed;
  };

  const maxConcurrentAgents = readInt(
    "MAX_CONCURRENT_AGENTS",
    limits.maxConcurrentAgents,
    1,
  );
  const healAttempts = readInt(
    "SANDCASTLE_HEAL_ATTEMPTS",
    limits.healAttempts,
    0,
  );

  return {
    ...limits,
    maxConcurrentAgents,
    healAttempts,
    plannerQueueDepth: Math.max(limits.plannerQueueDepth, maxConcurrentAgents),
  };
}

/** The branch an issue is worked on. Re-planning an issue must reproduce it exactly. */
export function issueBranch(git: GitConfig, issueId: string): string {
  return `${git.branchPrefix}${issueId}`;
}
