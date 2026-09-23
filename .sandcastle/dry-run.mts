// `--dry-run` — answer "what would this run actually do?" without doing it.
//
// A dry run resolves the config, renders the gate commands the prompts will be
// given, and asks GitHub for the issues the planner would see. It acquires
// NOTHING: no Docker, no model session, no sentinel port — so it is safe to run
// while a real orchestrator is going, and it is the one command that makes a
// config mistake (wrong label, wrong branch, a gate whose binary is misspelt)
// visible in a second rather than forty minutes in.
//
// Pure by injection: `gh` arrives as an exec function, so the tests below drive
// every branch — including the failure ones — with no network and no gh CLI.

import {
  formatGateCommands,
  gateCommand,
  PHASE_NAMES,
  type ResolvedLimits,
  type ResolvedModels,
  type SandcastleConfig,
} from "./config.mts";

/** Runs a command and returns its stdout; throws on a non-zero exit. */
export type CommandExec = (file: string, args: readonly string[]) => string;

export interface OpenIssue {
  readonly number: number;
  readonly title: string;
  /**
   * The issue's OPEN blockers, from GitHub's native issue dependencies. Closed
   * blockers are dropped while parsing — they no longer block anything — so an
   * empty array means "ready to be worked".
   */
  readonly blockedBy: readonly number[];
}

export type IssueQuery =
  | { readonly ok: true; readonly issues: readonly OpenIssue[] }
  /**
   * The listing failed or could not be understood. Reported, never swallowed
   * into an empty list: "gh is not authenticated" and "there is no work" must
   * not look the same on a screen a human is reading to decide whether to start
   * a night of agent runs.
   */
  | { readonly ok: false; readonly error: string };

function errorMessage(err: unknown): string {
  return err instanceof Error ? err.message : String(err);
}

function asRecord(value: unknown): Record<string, unknown> | undefined {
  return typeof value === "object" && value !== null && !Array.isArray(value)
    ? (value as Record<string, unknown>)
    : undefined;
}

/**
 * The issue nodes inside whatever `gh` printed: either a bare array (a
 * `gh issue list --json` listing) or the `issues` connection of a GraphQL
 * response, which is the only listing that also carries blocking edges.
 */
function issueNodes(parsed: unknown): readonly unknown[] | undefined {
  if (Array.isArray(parsed)) {
    return parsed;
  }
  const repository = asRecord(asRecord(asRecord(parsed)?.data)?.repository);
  const nodes = asRecord(repository?.issues)?.nodes;
  return Array.isArray(nodes) ? nodes : undefined;
}

type BlockerQuery =
  | { readonly ok: true; readonly blockedBy: number[] }
  | { readonly ok: false; readonly error: string };

/**
 * An issue's OPEN blockers, from the `blockedBy` connection.
 *
 * Fail-closed on anything unexpected: a listing that stopped reporting edges —
 * a renamed field, a permission the token lost — must not read as "nothing
 * blocks anything", which would queue issues whose prerequisites are unmerged.
 */
function openBlockers(issueNumber: number, raw: unknown): BlockerQuery {
  const nodes = asRecord(raw)?.nodes;
  if (!Array.isArray(nodes)) {
    return {
      ok: false,
      error:
        `gh returned issue #${issueNumber} without its blockedBy edges — the ` +
        "configured listing query must ask for them, because an unknown " +
        "blocker set is never treated as unblocked",
    };
  }

  const blockedBy: number[] = [];
  for (const node of nodes) {
    const blocker = asRecord(node);
    if (typeof blocker?.number !== "number" || typeof blocker.state !== "string") {
      return {
        ok: false,
        error:
          `gh returned a blocker of issue #${issueNumber} without a number and ` +
          "a state",
      };
    }
    // Closed blockers no longer block; only open ones hold an issue back.
    if (blocker.state.toUpperCase() === "OPEN") {
      blockedBy.push(blocker.number);
    }
  }
  return { ok: true, blockedBy };
}

/**
 * Turn the listing `gh` printed into issues plus their open blockers.
 *
 * Pure, so every shape a misconfigured query can produce is a unit test rather
 * than a surprise forty minutes into a round.
 */
export function parseIssueListing(stdout: string): IssueQuery {
  let parsed: unknown;
  try {
    parsed = JSON.parse(stdout.trim() === "" ? "[]" : stdout);
  } catch {
    return {
      ok: false,
      error:
        `gh did not return JSON (is the query missing --json?): ` +
        `${stdout.slice(0, 200)}`,
    };
  }

  // A GraphQL error envelope: gh exits non-zero on one, but a partial response
  // carries both `data` and `errors`, and half a backlog is not a backlog.
  const errors = asRecord(parsed)?.errors;
  if (Array.isArray(errors) && errors.length > 0) {
    const messages = errors
      .map((error) => String(asRecord(error)?.message ?? error))
      .join("; ");
    return { ok: false, error: `the issue query returned errors: ${messages}` };
  }

  const nodes = issueNodes(parsed);
  if (nodes === undefined) {
    return { ok: false, error: "gh returned JSON that is not an array of issues" };
  }

  const issues: OpenIssue[] = [];
  for (const entry of nodes) {
    const candidate = asRecord(entry);
    if (candidate === undefined) {
      return { ok: false, error: "gh returned an issue entry that is not an object" };
    }
    if (typeof candidate.number !== "number" || typeof candidate.title !== "string") {
      return {
        ok: false,
        error:
          "gh returned an issue without a number and a title — the configured " +
          "--json fields must include both",
      };
    }
    const blockers = openBlockers(candidate.number, candidate.blockedBy);
    if (!blockers.ok) {
      return blockers;
    }
    issues.push({
      number: candidate.number,
      title: candidate.title,
      blockedBy: blockers.blockedBy,
    });
  }
  return { ok: true, issues };
}

/**
 * Ask `gh` for the open, agent-ready issues, using the query from the config.
 *
 * The args are passed to `gh` WITHOUT a shell, so a label containing a space
 * cannot turn into two arguments and a config value can never become a command.
 */
export function listAgentIssues(
  listArgs: readonly string[],
  exec: CommandExec,
): IssueQuery {
  let stdout: string;
  try {
    stdout = exec("gh", listArgs);
  } catch (err) {
    return { ok: false, error: errorMessage(err) };
  }
  return parseIssueListing(stdout);
}

/**
 * Render the whole dry-run report.
 *
 * Deliberately one pure string: the test asserts on the report a human reads,
 * not on a pile of console.log calls.
 */
export function renderDryRun(
  config: SandcastleConfig,
  limits: ResolvedLimits,
  models: ResolvedModels,
  query: IssueQuery,
): string {
  const lines: string[] = [
    "",
    `Sandcastle dry run — ${config.project.name}`,
    "Nothing was started: no sandbox, no model session, no sentinel port.",
    "",
    "Configuration",
    `  integration branch   ${config.git.integrationBranch}`,
    `  issue branches       ${config.git.branchPrefix}<issue>`,
    `  issue label          ${config.issues.label}`,
    `  max iterations       ${limits.maxIterations}`,
    `  concurrent agents    ${limits.maxConcurrentAgents}`,
    `  planner queue depth  ${limits.plannerQueueDepth}`,
    `  idle timeout         ${limits.idleTimeoutSeconds}s`,
    `  healing attempts     ${limits.healAttempts}` +
      (limits.healAttempts === 0 ? " (healing is OFF)" : ""),
    "",
    "Models",
  ];

  // The RESOLVED models, so an `.env` override is visible before a round runs
  // on it; the marker says which fields did not come from the config file.
  for (const phase of PHASE_NAMES) {
    const model = models[phase];
    const marker =
      model.overridden.length === 0 ? "" : `  [.env: ${model.overridden.join(", ")}]`;
    lines.push(
      `  ${phase.padEnd(20)} ${model.model} (effort: ${model.effort})${marker}`,
    );
  }

  lines.push("", "Post-merge gates, in order");
  if (config.gates.length === 0) {
    lines.push("  (none configured — the merged HEAD would never be verified)");
  } else {
    for (const gate of config.gates) {
      lines.push(
        `  [${gate.name}] ${gateCommand(gate)}`,
        `      timeout ${Math.round(gate.timeoutMs / 60_000)} min · parser ${gate.parser}` +
          (gate.requiresDocker === true ? " · needs Docker" : ""),
      );
    }
  }

  lines.push("", "{{GATE_COMMANDS}} as the prompts will receive it", "");
  for (const line of formatGateCommands(config.gates).split("\n")) {
    lines.push(line === "" ? "" : `  ${line}`);
  }

  lines.push("", `Open issues labelled ${config.issues.label}`);
  if (!query.ok) {
    lines.push(
      `  COULD NOT LIST THEM: ${query.error}`,
      "  A real run's planner would fail the same way — fix this first.",
    );
  } else if (query.issues.length === 0) {
    lines.push("  (none — a real run would plan nothing and exit)");
  } else {
    for (const issue of query.issues) {
      // The blocked/unblocked mark is the native GitHub dependency edge, not a
      // guess: the planner reasons about more than this, but never less.
      const blocked =
        issue.blockedBy.length === 0
          ? "unblocked"
          : `BLOCKED by ${issue.blockedBy.map((number) => `#${number}`).join(", ")}`;
      lines.push(
        `  #${issue.number} ${issue.title}` +
          ` → ${config.git.branchPrefix}${issue.number} · ${blocked}`,
      );
    }
    const unblocked = query.issues.filter((issue) => issue.blockedBy.length === 0).length;
    lines.push(
      "",
      `  ${query.issues.length} issue(s) visible, ${unblocked} unblocked; a ` +
        `round would queue up to ${limits.plannerQueueDepth} of the unblocked ` +
        `ones and run ${limits.maxConcurrentAgents} at a time.`,
    );
  }
  lines.push("");

  return lines.join("\n");
}

/**
 * True when this invocation is a dry run.
 *
 * Both spellings exist because the flag is used in two places: by hand on the
 * command line, and from a script or CI step that can only set an environment
 * variable.
 */
export function isDryRun(
  argv: readonly string[],
  env: Readonly<Record<string, string | undefined>>,
): boolean {
  return argv.includes("--dry-run") || env.SANDCASTLE_DRY_RUN === "1";
}
