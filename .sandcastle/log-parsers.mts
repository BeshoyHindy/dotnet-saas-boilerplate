// Gate log parsers — turning a 40k-line gate log into something readable.
//
// Two audiences, two shapes, one parser per gate:
//   - `excerpt`  a handful of lines for the console, so a red run explains
//                itself without re-flooding the terminal the log file exists
//                to keep clean.
//   - `failures` self-contained blocks for briefing the healer agent: enough
//                to name the test, the assertion and the seam without making
//                it re-read the whole log.
//
// Which parser a gate uses is a CONFIG choice (`gates[].parser`), not a
// property of this pipeline: a repo whose gate is `cargo test` or `pytest`
// adds a parser here and names it in its config. Everything below is pure
// string → string[], so the tests need no gate, no log file and no Docker.

/** The parsers a gate may name. Extend both this union and LOG_PARSERS together. */
export type LogParserName = "generic" | "dotnetBuild" | "dotnetTest";

export interface LogParser {
  /** Short, console-sized summary of why the gate went red. */
  readonly excerpt: (content: string) => string[];
  /**
   * Self-contained failure blocks for an agent briefing. May be larger than
   * `excerpt`; never the whole log.
   */
  readonly failures: (content: string) => string[];
}

/** Cap on excerpt lines: a mass failure carries hundreds of marker lines. */
const EXCERPT_CAP = 40;
/** Cap on the number of failure blocks briefed to an agent. */
const FAILURE_BLOCK_CAP = 25;
/** Cap on the lines kept per failure block (message + the first test-side frames). */
const FAILURE_BLOCK_LINES = 14;

function splitLines(content: string): string[] {
  return content.split(/\r?\n/);
}

/** The last `EXCERPT_CAP` non-empty lines — the fallback every parser shares. */
function tail(content: string): string[] {
  return splitLines(content)
    .slice(-EXCERPT_CAP)
    .filter((line) => line.length > 0);
}

/**
 * Lines matching any `markers`, capped, with the tail as a fallback.
 *
 * The cap is load-bearing: a mass failure produces hundreds of matching lines
 * and printing them all defeats the point of writing the log to a file.
 */
function markerExcerpt(content: string, markers: readonly string[]): string[] {
  const matched = splitLines(content).filter((line) =>
    markers.some((marker) => line.includes(marker)),
  );
  if (matched.length === 0) {
    return tail(content);
  }
  if (matched.length > EXCERPT_CAP) {
    return [
      ...matched.slice(0, EXCERPT_CAP),
      `… ${matched.length - EXCERPT_CAP} more matching lines in the log.`,
    ];
  }
  return matched;
}

/** Markers a .NET build/test log uses to announce a failure. */
const DOTNET_MARKERS = [
  "error CS",
  "error MSB",
  "error NETSDK",
  "error TESTERROR",
  "Build FAILED",
  "Failed!",
  "Test summary:",
] as const;

/**
 * Extract each failed test as one self-contained block: the `Failed <name>`
 * line, its `Error Message:` lines, and the first stack frames that point at
 * a source file (`in /…/X.cs:line N`).
 *
 * Framework and async-machinery frames are dropped deliberately — they are the
 * bulk of a stack and none of the signal. Falls back to the marker excerpt when
 * the log carries no `Failed <name>` blocks at all (a build error, a crashed
 * test host): "no failed test" is not "nothing went wrong".
 */
function dotnetTestFailureBlocks(content: string): string[] {
  const lines = splitLines(content);
  const blocks: string[] = [];
  let current: string[] | null = null;
  let inStack = false;

  const flush = (): void => {
    if (current !== null && current.length > 0) {
      blocks.push(current.join("\n"));
    }
    current = null;
    inStack = false;
  };

  for (const raw of lines) {
    const line = raw.trimEnd();
    if (/^\s*Failed\s+\S+.*\[[\d.,]+ (ms|s)\]\s*$/.test(line)) {
      flush();
      current = [line.trim()];
      continue;
    }
    if (current === null) {
      continue;
    }
    if (/^\s*Stack Trace:\s*$/.test(line)) {
      inStack = true;
      continue;
    }
    // A blank line after the stack frames, or the next xUnit marker, ends the block.
    if (line.trim().length === 0 || /^\[xUnit\.net/.test(line)) {
      if (inStack) {
        flush();
      }
      continue;
    }
    if (inStack) {
      // Keep only frames that name a source file — the test and the code under
      // test — never the async-machinery frames.
      if (/ in \S+\.cs:line \d+/.test(line) && current.length < FAILURE_BLOCK_LINES) {
        current.push(line.trim());
      }
      continue;
    }
    if (current.length < FAILURE_BLOCK_LINES) {
      current.push(line.trim());
    }
  }
  flush();

  if (blocks.length === 0) {
    return markerExcerpt(content, DOTNET_MARKERS);
  }
  if (blocks.length > FAILURE_BLOCK_CAP) {
    return [
      ...blocks.slice(0, FAILURE_BLOCK_CAP),
      `… ${blocks.length - FAILURE_BLOCK_CAP} more failed tests in the log.`,
    ];
  }
  return blocks;
}

/** Knows nothing about the tool: the tail of the log is all any log guarantees. */
export const genericParser: LogParser = {
  excerpt: tail,
  failures: tail,
};

/** A .NET build or restore: compiler/MSBuild diagnostics, tail as a fallback. */
export const dotnetBuildParser: LogParser = {
  excerpt: (content) => markerExcerpt(content, DOTNET_MARKERS),
  failures: (content) => markerExcerpt(content, DOTNET_MARKERS),
};

/** `dotnet test`: per-test blocks for the briefing, summary lines for the console. */
export const dotnetTestParser: LogParser = {
  excerpt: (content) => markerExcerpt(content, DOTNET_MARKERS),
  failures: dotnetTestFailureBlocks,
};

export const LOG_PARSERS: Readonly<Record<LogParserName, LogParser>> = {
  generic: genericParser,
  dotnetBuild: dotnetBuildParser,
  dotnetTest: dotnetTestParser,
};

/**
 * The parser a gate named, falling back to the generic one.
 *
 * Fails SOFT on purpose, unlike the rest of the pipeline: an unknown parser
 * name must never stop a run that is otherwise fine — the worst case is a
 * less-targeted excerpt, and the full log path is always printed beside it.
 */
export function resolveLogParser(name: LogParserName | undefined): LogParser {
  return (name === undefined ? undefined : LOG_PARSERS[name]) ?? genericParser;
}
