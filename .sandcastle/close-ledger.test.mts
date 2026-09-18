import assert from "node:assert/strict";
import test from "node:test";

import {
  mergeLedger,
  parseLedger,
  partitionLedger,
  remainingLedger,
  serializeLedger,
  type PendingClose,
} from "./close-ledger.mts";

// Neutral fixtures: the branch prefix is a config value, so the tests use one
// of their own rather than whatever this repo happens to be configured with.
const entry = (id: string, tip = `tip-${id}`): PendingClose => ({
  id,
  branch: `agent/issue-${id}`,
  tip,
});

test("parseLedger round-trips what serializeLedger wrote", () => {
  const entries = [entry("11"), entry("12")];
  assert.deepEqual(parseLedger(serializeLedger(entries)), entries);
});

test("parseLedger answers empty for an absent, empty, or corrupt file", () => {
  assert.deepEqual(parseLedger(null), []);
  assert.deepEqual(parseLedger(""), []);
  assert.deepEqual(parseLedger("not json {"), []);
  assert.deepEqual(parseLedger('{"an":"object"}'), []);
});

test("parseLedger drops mis-shaped entries and keeps the sound ones", () => {
  const raw = JSON.stringify([
    entry("11"),
    { id: "12" }, // no tip — cannot be judged, must not survive
    { id: "", branch: "b", tip: "t" }, // empty id — cannot be closed
    { id: "13", branch: "agent/issue-13", tip: "" }, // empty tip
    "just-a-string",
    null,
  ]);
  assert.deepEqual(parseLedger(raw), [entry("11")]);
});

test("partitionLedger judges the recorded tip, not the branch name", () => {
  const landedTip = entry("11", "landed-tip");
  const waitingTip = entry("12", "unlanded-tip");
  const { landed, waiting } = partitionLedger(
    [landedTip, waitingTip],
    (tip) => tip === "landed-tip",
  );
  assert.deepEqual(landed, [landedTip]);
  assert.deepEqual(waiting, [waitingTip]);
});

test("mergeLedger preserves waiting entries from earlier rounds", () => {
  const earlier = entry("9", "old-round-tip");
  const rerun = entry("11", "new-tip-after-rework");
  const fresh = entry("20");
  // 11 is re-worked this round (its new tip supersedes); 9 still waits.
  const merged = mergeLedger([earlier, entry("11", "stale-tip")], [rerun, fresh]);
  assert.deepEqual(merged, [earlier, rerun, fresh]);
});

test("remainingLedger keeps a landed entry whose close FAILED, so it is retried", () => {
  const entries = [entry("11"), entry("12"), entry("13")];
  // 11 closed fine; 12's gh call failed; 13 has not landed (never attempted).
  const remaining = remainingLedger(entries, new Set(["11"]));
  assert.deepEqual(remaining, [entry("12"), entry("13")]);
});
