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
import { join } from "node:path";

import {
  defaultPathPresence,
  findDirtyMergerWorktrees,
  makeMergerDirtPatchPath,
  mergerDirtErrorBlock,
  mergerDirtScanErrorBlock,
  preserveMergerDirt,
  type GitExec,
  type PathPresenceProbe,
} from "./merger-dirt.mts";

test("findDirtyMergerWorktrees returns only dirty sandcastle-merger worktrees", () => {
  const calls: Array<{ args: string[]; cwd?: string }> = [];
  const exec: GitExec = (args, cwd) => {
    calls.push({ args, cwd });
    if (args[0] === "worktree" && args[1] === "list" && args[2] === "--porcelain") {
      return [
        "worktree /home/repo",
        "HEAD abc123",
        "branch refs/heads/trunk",
        "worktree /home/repo/.sandcastle/worktrees/sandcastle-merger-20260817-120000-abc",
        "HEAD def456",
        "branch refs/heads/sandcastle-merger-20260817-120000-abc",
        "worktree /home/repo/.sandcastle/worktrees/sandcastle-merger-20260817-130000-def",
        "HEAD fed654",
        "branch refs/heads/sandcastle-merger-20260817-130000-def",
        "worktree /tmp/other-worktree",
        "HEAD 111111",
        "detached",
        "",
      ].join("\n");
    }
    if (args[0] === "status" && args[1] === "--porcelain") {
      if (cwd?.endsWith("sandcastle-merger-20260817-120000-abc")) {
        return " M src/file.ts\n?? untracked.txt\n";
      }
      if (cwd?.endsWith("sandcastle-merger-20260817-130000-def")) {
        return ""; // clean
      }
    }
    throw new Error(`unexpected git call: ${args.join(" ")} in ${cwd}`);
  };

  const scan = findDirtyMergerWorktrees("/home/repo", exec);

  assert.deepEqual(scan.scanErrors, []);
  assert.equal(scan.dirty.length, 1);
  assert.equal(
    scan.dirty[0].worktreePath,
    "/home/repo/.sandcastle/worktrees/sandcastle-merger-20260817-120000-abc",
  );
  assert.deepEqual(scan.dirty[0].statusLines, [" M src/file.ts", "?? untracked.txt"]);
});

test("findDirtyMergerWorktrees skips a REMOVED worktree whose status throws", () => {
  // The fake path does not exist on disk, so the thrown status reads as
  // "removed between listing and scanning" — a legitimate skip, not an error.
  const exec: GitExec = (args, cwd) => {
    if (args[0] === "worktree" && args[1] === "list" && args[2] === "--porcelain") {
      return [
        "worktree /home/repo/.sandcastle/worktrees/sandcastle-merger-20260817-120000-abc",
        "HEAD def456",
        "worktree /home/repo/.sandcastle/worktrees/sandcastle-merger-20260817-130000-def",
        "HEAD fed654",
        "",
      ].join("\n");
    }
    if (args[0] === "status" && args[1] === "--porcelain") {
      if (cwd?.endsWith("sandcastle-merger-20260817-120000-abc")) {
        throw new Error("worktree removed");
      }
      if (cwd?.endsWith("sandcastle-merger-20260817-130000-def")) {
        return " M other.ts\n";
      }
    }
    throw new Error(`unexpected git call: ${args.join(" ")} in ${cwd}`);
  };

  const scan = findDirtyMergerWorktrees("/home/repo", exec);

  assert.deepEqual(scan.scanErrors, []);
  assert.equal(scan.dirty.length, 1);
  assert.ok(scan.dirty[0].worktreePath.endsWith("sandcastle-merger-20260817-130000-def"));
});

test("findDirtyMergerWorktrees FAILS CLOSED when status throws on a worktree that still exists", () => {
  // A real directory stands in for the worktree: the status failure can no
  // longer be read as "removed", so it must land in scanErrors — never be
  // swallowed into a clean-looking verdict.
  const dir = mkdtempSync(join(tmpdir(), "sandcastle-dirt-"));
  const worktreePath = join(dir, ".sandcastle/worktrees/sandcastle-merger-20260817-120000-abc");
  try {
    mkdirSync(worktreePath, { recursive: true });

    const exec: GitExec = (args) => {
      if (args[0] === "worktree" && args[1] === "list" && args[2] === "--porcelain") {
        return [`worktree ${worktreePath}`, "HEAD def456", ""].join("\n");
      }
      if (args[0] === "status" && args[1] === "--porcelain") {
        throw new Error("index locked");
      }
      throw new Error(`unexpected git call: ${args.join(" ")}`);
    };

    const scan = findDirtyMergerWorktrees("/home/repo", exec);

    assert.equal(scan.dirty.length, 0);
    assert.equal(scan.scanErrors.length, 1);
    assert.match(scan.scanErrors[0], /index locked/);
    assert.ok(scan.scanErrors[0].includes(worktreePath));
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("findDirtyMergerWorktrees FAILS CLOSED when the worktree's presence is UNKNOWN", () => {
  // existsSync would collapse an EACCES/EIO stat failure into "false" and the
  // worktree would be skipped as removed — the probe's "unknown" verdict must
  // land in scanErrors instead.
  const exec: GitExec = (args) => {
    if (args[0] === "worktree" && args[1] === "list" && args[2] === "--porcelain") {
      return [
        "worktree /home/repo/.sandcastle/worktrees/sandcastle-merger-20260817-120000-abc",
        "HEAD def456",
        "",
      ].join("\n");
    }
    if (args[0] === "status" && args[1] === "--porcelain") {
      throw new Error("permission denied");
    }
    throw new Error(`unexpected git call: ${args.join(" ")}`);
  };
  const presence: PathPresenceProbe = () => "unknown";

  const scan = findDirtyMergerWorktrees("/home/repo", exec, presence);

  assert.equal(scan.dirty.length, 0);
  assert.equal(scan.scanErrors.length, 1);
  assert.match(scan.scanErrors[0], /permission denied/);
});

test("findDirtyMergerWorktrees FAILS CLOSED when worktree list itself throws", () => {
  const exec: GitExec = () => {
    throw new Error("not a git repository");
  };

  const scan = findDirtyMergerWorktrees("/home/repo", exec);

  assert.equal(scan.dirty.length, 0);
  assert.equal(scan.scanErrors.length, 1);
  assert.match(scan.scanErrors[0], /worktree list failed/);
  assert.match(scan.scanErrors[0], /not a git repository/);
});

test("preserveMergerDirt writes the HEAD-relative diff to the patch file", () => {
  const dir = mkdtempSync(join(tmpdir(), "sandcastle-dirt-"));
  const patchPath = join(dir, "dirt.patch");

  const exec: GitExec = (args, cwd) => {
    if (args[0] === "worktree" && args[1] === "list" && args[2] === "--porcelain") {
      return [
        "worktree /home/repo/.sandcastle/worktrees/sandcastle-merger-20260817-120000-abc",
        "HEAD def456",
        "",
      ].join("\n");
    }
    if (args[0] === "status" && args[1] === "--porcelain") {
      return " M src/file.ts\n";
    }
    if (args[0] === "diff" && args[1] === "HEAD") {
      return "diff --git a/src/file.ts b/src/file.ts\n+change\n";
    }
    throw new Error(`unexpected git call: ${args.join(" ")} in ${cwd}`);
  };

  try {
    const scan = preserveMergerDirt("/home/repo", exec, patchPath);

    assert.deepEqual(scan.scanErrors, []);
    assert.equal(scan.reports.length, 1);
    assert.equal(scan.reports[0].patchPath, patchPath);
    assert.equal(scan.reports[0].diffError, null);
    assert.ok(existsSync(patchPath));
    const patch = readFileSync(patchPath, "utf8");
    assert.match(patch, /merger-dirt: \/home\/repo\/\.sandcastle\/worktrees\/sandcastle-merger-20260817-120000-abc/);
    assert.match(patch, /diff --git a\/src\/file.ts b\/src\/file.ts/);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("preserveMergerDirt returns patchPath null when the diff is empty (untracked only)", () => {
  const dir = mkdtempSync(join(tmpdir(), "sandcastle-dirt-"));
  const patchPath = join(dir, "dirt.patch");

  const exec: GitExec = (args) => {
    if (args[0] === "worktree" && args[1] === "list" && args[2] === "--porcelain") {
      return [
        "worktree /home/repo/.sandcastle/worktrees/sandcastle-merger-20260817-120000-abc",
        "HEAD def456",
        "",
      ].join("\n");
    }
    if (args[0] === "status" && args[1] === "--porcelain") {
      return "?? untracked-only.txt\n";
    }
    if (args[0] === "diff" && args[1] === "HEAD") {
      return "";
    }
    throw new Error(`unexpected git call: ${args.join(" ")}`);
  };

  try {
    const scan = preserveMergerDirt("/home/repo", exec, patchPath);

    assert.equal(scan.reports.length, 1);
    assert.equal(scan.reports[0].patchPath, null);
    assert.equal(scan.reports[0].diffError, null);
    // No tracked diff means no patch file should be created.
    assert.ok(!existsSync(patchPath));
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("preserveMergerDirt sets diffError when the capture fails on an EXISTING worktree", () => {
  const dir = mkdtempSync(join(tmpdir(), "sandcastle-dirt-"));
  const patchPath = join(dir, "dirt.patch");
  const worktreePath = join(dir, ".sandcastle/worktrees/sandcastle-merger-20260817-120000-abc");

  try {
    mkdirSync(worktreePath, { recursive: true });

    const exec: GitExec = (args) => {
      if (args[0] === "worktree" && args[1] === "list" && args[2] === "--porcelain") {
        return [`worktree ${worktreePath}`, "HEAD def456", ""].join("\n");
      }
      if (args[0] === "status" && args[1] === "--porcelain") {
        return " M src/file.ts\n";
      }
      if (args[0] === "diff" && args[1] === "HEAD") {
        throw new Error("object store corrupt");
      }
      throw new Error(`unexpected git call: ${args.join(" ")}`);
    };

    const scan = preserveMergerDirt("/home/repo", exec, patchPath);

    // The dirty worktree is still REPORTED (fail closed) — with the capture
    // failure carried so the error block warns against removing the worktree.
    assert.equal(scan.reports.length, 1);
    assert.equal(scan.reports[0].patchPath, null);
    assert.match(scan.reports[0].diffError ?? "", /object store corrupt/);
    assert.ok(!existsSync(patchPath));
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("preserveMergerDirt treats an UNKNOWN-presence diff failure as only-copy, never as removed", () => {
  const dir = mkdtempSync(join(tmpdir(), "sandcastle-dirt-"));
  const patchPath = join(dir, "dirt.patch");

  const exec: GitExec = (args) => {
    if (args[0] === "worktree" && args[1] === "list" && args[2] === "--porcelain") {
      return [
        "worktree /home/repo/.sandcastle/worktrees/sandcastle-merger-20260817-120000-abc",
        "HEAD def456",
        "",
      ].join("\n");
    }
    if (args[0] === "status" && args[1] === "--porcelain") {
      return " M src/file.ts\n";
    }
    if (args[0] === "diff" && args[1] === "HEAD") {
      throw new Error("permission denied");
    }
    throw new Error(`unexpected git call: ${args.join(" ")}`);
  };
  const presence: PathPresenceProbe = () => "unknown";

  try {
    const scan = preserveMergerDirt("/home/repo", exec, patchPath, presence);

    assert.equal(scan.reports.length, 1);
    assert.equal(scan.reports[0].patchPath, null);
    // The message must carry the real failure, not the "removed" claim that
    // would invite deleting the only copy.
    assert.match(scan.reports[0].diffError ?? "", /permission denied/);
    assert.doesNotMatch(scan.reports[0].diffError ?? "", /removed/);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("defaultPathPresence distinguishes present from absent", () => {
  const dir = mkdtempSync(join(tmpdir(), "sandcastle-dirt-"));
  try {
    assert.equal(defaultPathPresence(dir), "present");
    assert.equal(defaultPathPresence(join(dir, "never-created")), "absent");
    // A path COMPONENT that is a FILE, not a directory (ENOTDIR), also proves absence.
    writeFileSync(join(dir, "a-file"), "x");
    assert.equal(defaultPathPresence(join(dir, "a-file", "child")), "absent");
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("preserveMergerDirt appends diffs from two dirty worktrees into one patch", () => {
  const dir = mkdtempSync(join(tmpdir(), "sandcastle-dirt-"));
  const patchPath = join(dir, "dirt.patch");

  const exec: GitExec = (args, cwd) => {
    if (args[0] === "worktree" && args[1] === "list" && args[2] === "--porcelain") {
      return [
        "worktree /home/repo/.sandcastle/worktrees/sandcastle-merger-20260817-120000-abc",
        "HEAD def456",
        "worktree /home/repo/.sandcastle/worktrees/sandcastle-merger-20260817-130000-def",
        "HEAD fed654",
        "",
      ].join("\n");
    }
    if (args[0] === "status" && args[1] === "--porcelain") {
      return " M src/file.ts\n";
    }
    if (args[0] === "diff" && args[1] === "HEAD") {
      if (cwd?.endsWith("sandcastle-merger-20260817-120000-abc")) {
        return "diff --git a/src/file.ts b/src/file.ts\n+first\n";
      }
      if (cwd?.endsWith("sandcastle-merger-20260817-130000-def")) {
        return "diff --git a/src/other.ts b/src/other.ts\n+second\n";
      }
    }
    throw new Error(`unexpected git call: ${args.join(" ")} in ${cwd}`);
  };

  try {
    const scan = preserveMergerDirt("/home/repo", exec, patchPath);

    assert.equal(scan.reports.length, 2);
    assert.equal(scan.reports[0].patchPath, patchPath);
    assert.equal(scan.reports[1].patchPath, patchPath);
    const patch = readFileSync(patchPath, "utf8");
    assert.match(patch, /\+first/);
    assert.match(patch, /\+second/);
    assert.ok(patch.indexOf("merger-dirt:") !== patch.lastIndexOf("merger-dirt:"));
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("preserveMergerDirt propagates scan errors from the worktree listing", () => {
  const exec: GitExec = () => {
    throw new Error("not a git repository");
  };

  const scan = preserveMergerDirt("/home/repo", exec, "/nonexistent/never-written.patch");

  assert.equal(scan.reports.length, 0);
  assert.equal(scan.scanErrors.length, 1);
  assert.match(scan.scanErrors[0], /worktree list failed/);
});

test("mergerDirtErrorBlock names the worktree, status lines, patch path, and gate skip", () => {
  const block = mergerDirtErrorBlock([
    {
      worktreePath: "/home/repo/.sandcastle/worktrees/sandcastle-merger-20260817-120000-abc",
      statusLines: [" M src/file.ts", "?? untracked.txt"],
      patchPath: "/home/repo/.sandcastle/logs/20260817-120000-merger-dirt.patch",
      diffError: null,
    },
  ]);

  assert.match(block, /MERGER LEFT UNCOMMITTED WORK/);
  assert.match(block, /sandcastle-merger-20260817-120000-abc/);
  assert.match(block, /src\/file\.ts/);
  assert.match(block, /untracked\.txt/);
  assert.match(block, /20260817-120000-merger-dirt\.patch/);
  assert.match(block, /post-merge gate was NOT run/);
  assert.match(block, /git apply/);
  assert.match(block, /git worktree remove --force/);
});

test("mergerDirtErrorBlock handles only-untracked worktrees with null patchPath", () => {
  const block = mergerDirtErrorBlock([
    {
      worktreePath: "/home/repo/.sandcastle/worktrees/sandcastle-merger-20260817-120000-abc",
      statusLines: ["?? untracked-only.txt"],
      patchPath: null,
      diffError: null,
    },
  ]);

  assert.match(block, /untracked-only\.txt/);
  assert.match(block, /No tracked diff was captured/);
  assert.match(block, /post-merge gate was NOT run/);
});

test("mergerDirtErrorBlock warns that a failed-capture worktree is the only copy", () => {
  const block = mergerDirtErrorBlock([
    {
      worktreePath: "/home/repo/.sandcastle/worktrees/sandcastle-merger-20260817-120000-abc",
      statusLines: [" M src/file.ts"],
      patchPath: null,
      diffError: "object store corrupt",
    },
  ]);

  assert.match(block, /Diff capture FAILED/);
  assert.match(block, /object store corrupt/);
  assert.match(block, /ONLY copy/);
  assert.match(block, /do NOT remove it/);
});

test("mergerDirtScanErrorBlock refuses to claim clean and lists the failures", () => {
  const block = mergerDirtScanErrorBlock([
    "git worktree list failed in /home/repo: not a git repository",
  ]);

  assert.match(block, /COULD NOT VERIFY/);
  assert.match(block, /not a git repository/);
  assert.match(block, /post-merge gate was NOT run/);
  assert.match(block, /rerun Sandcastle/i);
});

test("makeMergerDirtPatchPath produces the required filename shape", () => {
  const path = makeMergerDirtPatchPath(new Date(2026, 7, 16, 14, 30, 45));
  assert.match(path, /\.sandcastle\/logs\/\d{8}-\d{6}-merger-dirt\.patch$/);
  assert.match(path, /20260816-143045-merger-dirt\.patch$/);
});
