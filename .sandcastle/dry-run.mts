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
  type ResolvedLimits,
  type SandcastleConfig,
} from "./config.mts";

/** Runs a command and returns its stdout; throws on a non-zero exit. */
export type CommandExec = (file: string, args: readonly string[]) => string;

export interface OpenIssue {
  readonly number: number;
  readonly title: string;
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
  if (!Array.isArray(parsed)) {
    return { ok: false, error: "gh returned JSON that is not an array of issues" };
  }

  const issues: OpenIssue[] = [];
  for (const entry of parsed) {
    if (typeof entry !== "object" || entry === null) {
      return { ok: false, error: "gh returned an issue entry that is not an object" };
    }
    const candidate = entry as Record<string, unknown>;
    if (typeof candidate.number !== "number" || typeof candidate.title !== "string") {
      return {
        ok: false,
        error:
          "gh returned an issue without a number and a title — the configured " +
          "--json fields must include both",
      };
    }
    issues.push({ number: candidate.number, title: candidate.title });
  }
  return { ok: true, issues };
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

  for (const [phase, model] of Object.entries(config.models)) {
    lines.push(
      `  ${phase.padEnd(20)} ${model.model}` +
        (model.effort === undefined ? "" : ` (effort: ${model.effort})`),
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
      lines.push(
        `  #${issue.number} ${issue.title}` +
          ` → ${config.git.branchPrefix}${issue.number}`,
      );
    }
    lines.push(
      "",
      `  ${query.issues.length} issue(s) visible; a round would queue up to ` +
        `${limits.plannerQueueDepth} of the unblocked ones and run ` +
        `${limits.maxConcurrentAgents} at a time.`,
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
