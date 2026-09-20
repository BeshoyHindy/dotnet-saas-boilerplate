import assert from "node:assert/strict";
import test from "node:test";
import {
  existsSync,
  mkdirSync,
  mkdtempSync,
  readFileSync,
  rmSync,
  writeFileSync,
} from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";

import {
  logShowsReaperStartFailure,
  makePostMergeGateLogPath,
  readGateExcerpt,
  readGateFailures,
  runPostMergeGates,
  survivorWarning,
  type PostMergeGate,
  type PostMergeGateExec,
} from "./post-merge-gate.mts";

// The gate list is injected, never imported: this module must not know which
// gates this repository happens to run.
const dockerGate: PostMergeGate = {
  name: "full test suite",
  file: "dotnet",
  args: ["test"],
  timeoutMs: 1_000,
  parser: "dotnetTest",
  requiresDocker: true,
};

const passingExec: PostMergeGateExec = (_file, _args, _timeoutMs, logPath) => ({
  ok: true,
  logPath,
});

const failingExec =
  (error?: Error): PostMergeGateExec =>
  (_file, _args, _timeoutMs, logPath) => ({
    ok: false,
    logPath,
    ...(error ? { error } : {}),
  });

test("returns ok when every post-merge gate passes", async () => {
  const result = await runPostMergeGates(
    [
      { name: "first", file: "one", args: [], timeoutMs: 1_000 },
      { name: "second", file: "two", args: [], timeoutMs: 2_000 },
    ],
    passingExec,
  );

  assert.equal(result.ok, true);
});

test("returns the first failed gate and does not invoke later gates", async () => {
  const invoked: string[] = [];
  const result = await runPostMergeGates(
    [
      { name: "passes", file: "one", args: [], timeoutMs: 1_000 },
      { name: "fails", file: "two", args: [], timeoutMs: 2_000 },
      { name: "never runs", file: "three", args: [], timeoutMs: 3_000 },
    ],
    (file, _args, _timeoutMs, logPath) => {
      invoked.push(file);
      if (file === "two") {
        return { ok: false, logPath };
      }
      return { ok: true, logPath };
    },
  );

  assert.equal(result.ok, false);
  assert.equal(result.ok === false && result.failed, "fails");
  assert.deepEqual(invoked, ["one", "two"]);
});

test("reports the failed gate's parser so the caller reads its log the right way", async () => {
  const result = await runPostMergeGates(
    [
      { name: "build", file: "one", args: [], timeoutMs: 1_000, parser: "dotnetBuild" },
      dockerGate,
    ],
    (file, _args, _timeoutMs, logPath) =>
      file === "one" ? { ok: true, logPath } : { ok: false, logPath },
    () => true,
  );

  assert.equal(result.ok === false && result.failed, "full test suite");
  assert.equal(result.ok === false && result.parser, "dotnetTest");
});

// Docker dying mid-run once failed an entire integration suite, and the run
// announced "the merged HEAD is red. Land the fix" — a defect hunt for a defect
// that did not exist.
test("reports a Docker-backed gate as unverified when Docker is unreachable", async () => {
  const result = await runPostMergeGates(
    [dockerGate],
    failingExec(new Error("every integration test failed")),
    () => false,
  );

  assert.equal(result.ok, false);
  assert.equal(result.ok === false && result.failed, "full test suite");
  assert.equal(result.ok === false && typeof result.logPath, "string");
  assert.match(
    result.ok === false ? String(result.unverified) : "",
    /Docker daemon is not reachable/,
  );
});

test("reports a genuine failure as red when Docker is still reachable", async () => {
  const result = await runPostMergeGates(
    [dockerGate],
    // A clean non-zero exit and no spawn error: the suite ran and delivered a
    // red verdict — the only shape that may be reported as a broken HEAD.
    failingExec(),
    () => true,
  );

  assert.equal(result.ok, false);
  assert.equal(result.ok === false && result.failed, "full test suite");
  assert.equal(result.ok === false && result.unverified, undefined);
  assert.equal(result.ok === false && typeof result.logPath, "string");
});

test("reports an exec error as unverified even when Docker is reachable", async () => {
  // ENOENT, an fs error opening the log, or the timeout kill: the child never
  // delivered a verdict, so the HEAD must not be called red.
  const result = await runPostMergeGates(
    [dockerGate],
    failingExec(new Error("spawn dotnet ENOENT")),
    () => true,
  );

  assert.equal(result.ok, false);
  assert.equal(result.ok === false && result.failed, "full test suite");
  assert.match(
    result.ok === false ? String(result.unverified) : "",
    /did not produce a verdict.*spawn dotnet ENOENT/,
  );
});

const REAPER_LOG =
  "  Failed Integration.Tests.Tests.SecurityHeadersTests.X [1 ms]\n" +
  "  Error Message:\n" +
  "   DotNet.Testcontainers.Containers.ResourceReaperException : Initialization has been cancelled.\n" +
  "Failed!  - Failed:    11, Passed:     0, Skipped:     0, Total:    11\n";

/**
 * A red exec that writes `content` to the gate log, so the retry decision reads
 * a real file. The real exec creates the logs directory itself; this stand-in
 * has to, because `.sandcastle/logs/` is git-ignored and absent in a fresh
 * clone (and in CI).
 */
const redExecWritingLog =
  (content: string, invoked: string[]): PostMergeGateExec =>
  (_file, _args, _timeoutMs, logPath) => {
    invoked.push(logPath);
    mkdirSync(dirname(logPath), { recursive: true });
    writeFileSync(logPath, content);
    return { ok: false, logPath };
  };

test("logShowsReaperStartFailure reads the reaper marker and tolerates a missing log", () => {
  const dir = mkdtempSync(join(tmpdir(), "gate-reaper-"));
  try {
    const withMarker = join(dir, "red.log");
    writeFileSync(withMarker, REAPER_LOG);
    const without = join(dir, "plain.log");
    writeFileSync(without, "Failed!  - Failed: 1, Passed: 0\n");

    assert.equal(logShowsReaperStartFailure(withMarker), true);
    assert.equal(logShowsReaperStartFailure(without), false);
    assert.equal(logShowsReaperStartFailure(join(dir, "missing.log")), false);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("re-runs a Docker gate once when the red log shows the reaper failing to start, and believes a green rerun", async () => {
  const invoked: string[] = [];
  const result = await runPostMergeGates(
    [dockerGate],
    (file, args, timeoutMs, logPath) => {
      if (invoked.length === 0) {
        return redExecWritingLog(REAPER_LOG, invoked)(file, args, timeoutMs, logPath);
      }
      invoked.push(logPath);
      return { ok: true, logPath };
    },
    () => true,
  );

  try {
    assert.equal(invoked.length, 2, "exactly one retry");
    assert.notEqual(invoked[0], invoked[1], "the rerun writes its own log");
    assert.equal(result.ok, true);
  } finally {
    for (const logPath of invoked) {
      rmSync(logPath, { force: true });
    }
  }
});

test("a rerun that is red again is the verdict — no third attempt, HEAD is red", async () => {
  const invoked: string[] = [];
  const result = await runPostMergeGates(
    [dockerGate],
    redExecWritingLog(REAPER_LOG, invoked),
    () => true,
  );

  try {
    assert.equal(invoked.length, 2);
    assert.equal(result.ok, false);
    assert.equal(result.ok === false && result.failed, "full test suite");
    assert.equal(
      result.ok === false && result.unverified,
      undefined,
      "a repeated red is a real verdict",
    );
  } finally {
    for (const logPath of invoked) {
      rmSync(logPath, { force: true });
    }
  }
});

test("does not re-run a red gate whose log has no reaper marker", async () => {
  const invoked: string[] = [];
  const result = await runPostMergeGates(
    [dockerGate],
    redExecWritingLog("Failed!  - Failed: 5, Passed: 853\n", invoked),
    () => true,
  );

  try {
    assert.equal(invoked.length, 1, "a plain red verdict is believed the first time");
    assert.equal(result.ok, false);
  } finally {
    for (const logPath of invoked) {
      rmSync(logPath, { force: true });
    }
  }
});

test("does not probe Docker for a gate that does not need it", async () => {
  let probed = false;

  const result = await runPostMergeGates(
    [{ name: "lint", file: "pnpm", args: ["lint"], timeoutMs: 1_000 }],
    failingExec(),
    () => {
      probed = true;
      return false;
    },
  );

  assert.equal(probed, false);
  assert.equal(result.ok, false);
  assert.equal(result.ok === false && result.failed, "lint");
});

test("does not probe Docker when every gate passes", async () => {
  let probed = false;

  const result = await runPostMergeGates([dockerGate], passingExec, () => {
    probed = true;
    return false;
  });

  assert.equal(probed, false);
  assert.equal(result.ok, true);
});

test("passes each gate's declared command, timeout, and log path to the executor", async () => {
  const calls: Array<{
    file: string;
    args: readonly string[];
    timeoutMs: number;
    logPath: string;
  }> = [];

  await runPostMergeGates(
    [
      {
        name: "declared gate",
        file: "declared-command",
        args: ["first-arg", "second-arg"],
        timeoutMs: 12_345,
      },
    ],
    (file, args, timeoutMs, logPath) => {
      calls.push({ file, args, timeoutMs, logPath });
      return { ok: true, logPath };
    },
  );

  assert.equal(calls.length, 1);
  assert.equal(calls[0]!.file, "declared-command");
  assert.deepEqual(calls[0]!.args, ["first-arg", "second-arg"]);
  assert.equal(calls[0]!.timeoutMs, 12_345);
  assert.match(calls[0]!.logPath, /\.sandcastle\/logs\/\d{8}-\d{6}-post-merge-gate\.log$/);
});

test("production exec streams stdout and stderr to a log file", async () => {
  const result = await runPostMergeGates([
    {
      name: "stdout + stderr capture",
      file: "node",
      args: [
        "-e",
        "console.log('stdout line'); console.error('stderr line'); process.exit(1);",
      ],
      timeoutMs: 5_000,
    },
  ]);

  assert.equal(result.ok, false);
  const logPath = result.ok === false ? result.logPath : undefined;
  assert.ok(logPath && existsSync(logPath), "log file should exist");

  if (logPath) {
    const log = readGateExcerpt(logPath);
    assert.ok(log.some((line) => line.includes("stdout line")));
    assert.ok(log.some((line) => line.includes("stderr line")));
    rmSync(logPath, { force: true });
  }
});

test("readGateExcerpt and readGateFailures use the named parser", () => {
  const dir = mkdtempSync(join(tmpdir(), "sandcastle-gate-"));
  const logPath = join(dir, "test.log");
  writeFileSync(
    logPath,
    [
      "Build started.",
      "  Failed Acme.Tests.Thing.Works [3 ms]",
      "  Error Message:",
      "   boom",
      "",
      "Failed!  - Failed:     1, Passed:   499",
      "",
    ].join("\n"),
    "utf8",
  );

  try {
    // The console excerpt stays summary-sized…
    assert.deepEqual(readGateExcerpt(logPath, "dotnetTest"), [
      "Failed!  - Failed:     1, Passed:   499",
    ]);
    // …while the healer briefing gets the whole failure block.
    const blocks = readGateFailures(logPath, "dotnetTest");
    assert.equal(blocks.length, 1);
    assert.match(blocks[0]!, /Failed Acme\.Tests\.Thing\.Works \[3 ms\]/);
    assert.match(blocks[0]!, /boom/);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("reading a log that was never written returns nothing rather than throwing", () => {
  const missing = join(tmpdir(), "sandcastle-gate-missing", "no-such.log");
  assert.deepEqual(readGateExcerpt(missing, "dotnetTest"), []);
  assert.deepEqual(readGateFailures(missing, "dotnetTest"), []);
});

test("production exec detects a timeout as a failure", async () => {
  const result = await runPostMergeGates([
    {
      name: "timeout gate",
      file: "node",
      args: ["-e", "setTimeout(() => {}, 10_000);"],
      timeoutMs: 100,
    },
  ]);

  assert.equal(result.ok, false);
  assert.equal(result.ok === false && result.failed, "timeout gate");
  assert.ok(result.ok === false && result.error !== undefined);
  assert.match(
    result.ok === false && result.error ? result.error.message : "",
    /ETIMEDOUT/,
  );
  // A killed child never delivered a verdict — the HEAD is unverified, not red.
  assert.match(
    result.ok === false ? String(result.unverified) : "",
    /did not produce a verdict/,
  );

  const logPath = result.ok === false ? result.logPath : undefined;
  if (logPath) {
    rmSync(logPath, { force: true });
  }
});

test("production exec classifies a signal-killed gate as unverified, not red", async () => {
  const result = await runPostMergeGates([
    {
      name: "signal gate",
      file: "node",
      args: ["-e", 'process.kill(process.pid, "SIGKILL");'],
      timeoutMs: 10_000,
    },
  ]);

  assert.equal(result.ok, false);
  assert.equal(result.ok === false && result.failed, "signal gate");
  assert.match(
    result.ok === false && result.error ? result.error.message : "",
    /killed by signal SIGKILL/,
  );
  assert.match(
    result.ok === false ? String(result.unverified) : "",
    /did not produce a verdict/,
  );

  const logPath = result.ok === false ? result.logPath : undefined;
  if (logPath) {
    rmSync(logPath, { force: true });
  }
});

test("propagates a user interrupt as interrupted, never as red", async () => {
  const result = await runPostMergeGates(
    [dockerGate],
    (_file, _args, _timeoutMs, logPath) => ({
      ok: false,
      logPath,
      interrupted: true,
      error: new Error("the gate run was interrupted by SIGINT"),
    }),
    () => true,
  );

  assert.equal(result.ok, false);
  assert.equal(result.ok === false && result.interrupted, true);
  assert.match(
    result.ok === false ? String(result.unverified) : "",
    /interrupted before the gate delivered a verdict/,
  );
});

test("production exec kills the whole process tree on timeout", async () => {
  // The gate child spawns a grandchild; a parent-only kill would orphan it.
  const dir = mkdtempSync(join(tmpdir(), "sandcastle-gate-"));
  const pidFile = join(dir, "grandchild.pid");

  try {
    const result = await runPostMergeGates([
      {
        name: "orphan gate",
        file: "bash",
        args: ["-c", `sleep 100 & echo $! > "${pidFile}"; wait`],
        timeoutMs: 500,
      },
    ]);

    assert.equal(result.ok, false);
    assert.match(
      result.ok === false && result.error ? result.error.message : "",
      /ETIMEDOUT/,
    );

    const grandchildPid = Number(readFileSync(pidFile, "utf8").trim());
    assert.ok(Number.isInteger(grandchildPid) && grandchildPid > 0);
    // Give the group SIGTERM a beat to land, then assert the grandchild died.
    await new Promise((r) => setTimeout(r, 300));
    assert.throws(
      () => process.kill(grandchildPid, 0),
      /ESRCH/,
      "the grandchild must not survive the gate timeout",
    );

    const logPath = result.ok === false ? result.logPath : undefined;
    if (logPath) {
      rmSync(logPath, { force: true });
    }
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("production exec reaps a SIGTERM-ignoring grandchild before resolving", async () => {
  // The unref'd grace timer never fires once main.mts exits — the guarantee
  // must hold BY the time runPostMergeGates resolves, not ten seconds later.
  const dir = mkdtempSync(join(tmpdir(), "sandcastle-gate-"));
  const pidFile = join(dir, "stubborn.pid");

  try {
    const result = await runPostMergeGates([
      {
        name: "stubborn gate",
        file: "bash",
        args: ["-c", `bash -c 'trap "" TERM; sleep 100' & echo $! > "${pidFile}"; wait`],
        timeoutMs: 500,
      },
    ]);

    assert.equal(result.ok, false);

    const stubbornPid = Number(readFileSync(pidFile, "utf8").trim());
    assert.ok(Number.isInteger(stubbornPid) && stubbornPid > 0);
    // A short beat for the close-time group SIGKILL to be delivered.
    await new Promise((r) => setTimeout(r, 300));
    assert.throws(
      () => process.kill(stubbornPid, 0),
      /ESRCH/,
      "a TERM-ignoring group member must be SIGKILL'd before resolution",
    );

    const logPath = result.ok === false ? result.logPath : undefined;
    if (logPath) {
      rmSync(logPath, { force: true });
    }
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("production exec reaps stragglers even on a clean red exit", async () => {
  // A crashed test host can outlive the runner's own non-zero exit while still
  // holding its containers — the reap must not be gated on HOW the child
  // ended, and the red verdict must survive the cleanup.
  const dir = mkdtempSync(join(tmpdir(), "sandcastle-gate-"));
  const pidFile = join(dir, "straggler.pid");

  try {
    const result = await runPostMergeGates([
      {
        name: "red gate with straggler",
        file: "bash",
        args: ["-c", `bash -c 'trap "" TERM; sleep 100' & echo $! > "${pidFile}"; exit 7`],
        timeoutMs: 10_000,
      },
    ]);

    // Still a genuine red verdict — no error, no unverified reclassification.
    assert.equal(result.ok, false);
    assert.equal(result.ok === false && result.unverified, undefined);
    assert.equal(result.ok === false && result.error, undefined);

    const stragglerPid = Number(readFileSync(pidFile, "utf8").trim());
    assert.ok(Number.isInteger(stragglerPid) && stragglerPid > 0);
    await new Promise((r) => setTimeout(r, 300));
    assert.throws(
      () => process.kill(stragglerPid, 0),
      /ESRCH/,
      "a straggler must not survive a red exit",
    );

    const logPath = result.ok === false ? result.logPath : undefined;
    if (logPath) {
      rmSync(logPath, { force: true });
    }
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("surfaces a cleanup warning on a green run instead of swallowing it", async () => {
  const result = await runPostMergeGates(
    [{ name: "full test suite", file: "dotnet", args: ["test"], timeoutMs: 1_000 }],
    (_file, _args, _timeoutMs, logPath) => ({
      ok: true,
      logPath,
      warning: "the gate's process group still had survivors after 10000 ms",
    }),
  );

  assert.equal(result.ok, true);
  assert.match(result.ok ? String(result.warning) : "", /\[full test suite\].*survivors/);
});

test("carries the cleanup warning through a red verdict", async () => {
  const result = await runPostMergeGates(
    [dockerGate],
    (_file, _args, _timeoutMs, logPath) => ({
      ok: false,
      logPath,
      warning: "survivors remain",
    }),
    () => true,
  );

  assert.equal(result.ok, false);
  assert.equal(result.ok === false && result.unverified, undefined);
  assert.equal(result.ok === false && result.warning, "survivors remain");
});

test("survivorWarning names the group and the manual inspection command", () => {
  assert.match(survivorWarning(4242), /survivors after 10000 ms.*pgrep -g 4242/);
  assert.match(survivorWarning(undefined), /pgrep -g \?/);
});

test("a REAL SIGINT mid-run interrupts the gate, reaps the group, exits 130", async () => {
  // End-to-end through a subprocess: the orchestrator process (harness) gets a
  // real SIGINT while the gate child is running — no mocks anywhere.
  const dir = mkdtempSync(join(tmpdir(), "sandcastle-gate-"));
  const pidFile = join(dir, "gate-child.pid");
  const harnessPath = join(dir, "harness.mts");
  const modulePath = new URL("./post-merge-gate.mts", import.meta.url).pathname;
  writeFileSync(
    harnessPath,
    [
      `import { runPostMergeGates } from ${JSON.stringify(modulePath)};`,
      `import { writeSync } from "node:fs";`,
      `const result = await runPostMergeGates([{`,
      `  name: "sig gate",`,
      `  file: "bash",`,
      `  args: ["-c", 'sleep 30 & echo $! > ${JSON.stringify(pidFile).slice(1, -1)}; wait'],`,
      `  timeoutMs: 25_000,`,
      `}]);`,
      `if (!result.ok && result.interrupted) {`,
      `  writeSync(2, "INTERRUPTED-AS-EXPECTED\\n");`,
      `  process.exit(130);`,
      `}`,
      `process.exit(result.ok ? 0 : 1);`,
      "",
    ].join("\n"),
    "utf8",
  );

  try {
    const { spawn } = await import("node:child_process");
    const harness = spawn(process.execPath, ["--import", "tsx", harnessPath], {
      stdio: ["ignore", "ignore", "pipe"],
    });
    let stderr = "";
    harness.stderr.on("data", (chunk: Buffer) => (stderr += chunk.toString()));

    // Wait for the gate child to be running (its pid file appears).
    const deadline = Date.now() + 10_000;
    while (!existsSync(pidFile) && Date.now() < deadline) {
      await new Promise((r) => setTimeout(r, 50));
    }
    assert.ok(existsSync(pidFile), "the gate child never started");
    // Let the pid finish writing, then interrupt the ORCHESTRATOR for real.
    await new Promise((r) => setTimeout(r, 200));
    harness.kill("SIGINT");

    const exitCode: number | null = await new Promise((resolveExit) => {
      harness.once("close", (code) => resolveExit(code));
    });

    assert.equal(exitCode, 130, `expected 130, got ${exitCode}; stderr: ${stderr}`);
    assert.match(stderr, /INTERRUPTED-AS-EXPECTED/);

    const gateChildPid = Number(readFileSync(pidFile, "utf8").trim());
    assert.ok(Number.isInteger(gateChildPid) && gateChildPid > 0);
    assert.throws(
      () => process.kill(gateChildPid, 0),
      /ESRCH/,
      "the gate's group must be reaped on a real interrupt",
    );
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("writeStderrSync survives a broken stderr pipe without breaking flow", async () => {
  // Regression for the reap paths: writeSync(2, ...) throws EPIPE once the
  // pipe's read end is gone — the guard must swallow it so a failed log line
  // can never crash a signal handler mid-reap or skip an exit path.
  const dir = mkdtempSync(join(tmpdir(), "sandcastle-gate-"));
  const harnessPath = join(dir, "stderr-harness.mts");
  const modulePath = new URL("./post-merge-gate.mts", import.meta.url).pathname;
  writeFileSync(
    harnessPath,
    [
      `import { writeStderrSync } from ${JSON.stringify(modulePath)};`,
      `import { writeSync } from "node:fs";`,
      `await new Promise((r) => setTimeout(r, 500));`, // parent destroys stderr
      `writeStderrSync("this write hits a broken pipe\\n");`,
      `writeStderrSync("and so does this one\\n");`,
      // writeSync, NOT process.stdout.write: an async stdout write queued on a
      // pipe is dropped by process.exit(), failing the assertion even though
      // the guard held — the exact truncation bug this suite regression-tests.
      `writeSync(1, "GUARD-HELD\\n");`,
      `process.exit(0);`,
      "",
    ].join("\n"),
    "utf8",
  );

  try {
    const { spawn } = await import("node:child_process");
    const harness = spawn(process.execPath, ["--import", "tsx", harnessPath], {
      stdio: ["ignore", "pipe", "pipe"],
    });
    let stdout = "";
    harness.stdout.on("data", (chunk: Buffer) => (stdout += chunk.toString()));
    // Break the child's stderr: destroy the read end before it writes.
    harness.stderr.destroy();

    const exitCode: number | null = await new Promise((resolveExit) => {
      harness.once("close", (code) => resolveExit(code));
    });

    assert.equal(exitCode, 0, `guard failed; stdout: ${stdout}`);
    assert.match(stdout, /GUARD-HELD/);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("makePostMergeGateLogPath produces the required filename shape", () => {
  const path = makePostMergeGateLogPath(new Date(2026, 7, 16, 14, 30, 45));
  assert.match(path, /\.sandcastle\/logs\/20260816-143045-post-merge-gate\.log$/);
});

test("runPostMergeGates tags every log of the invocation with the given suffix", async () => {
  const paths: string[] = [];
  await runPostMergeGates(
    [{ name: "g", file: "one", args: [], timeoutMs: 1_000 }],
    passingExec,
    undefined,
    (_gate, logPath) => {
      paths.push(logPath);
    },
    "-heal-2",
  );
  assert.equal(paths.length, 1);
  assert.match(paths[0]!, /-heal-2-post-merge-gate\.log$/);
});
