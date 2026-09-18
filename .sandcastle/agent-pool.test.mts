import { test } from "node:test";
import assert from "node:assert/strict";

import { mapWithConcurrency } from "./agent-pool.mts";

/** Resolve after `ticks` macrotask turns, so ordering is deterministic. */
const after = async (ticks: number) => {
  for (let i = 0; i < ticks; i++) {
    await new Promise((resolve) => setTimeout(resolve, 0));
  }
};

test("runs every item and preserves input order in the results", async () => {
  const results = await mapWithConcurrency([1, 2, 3, 4, 5], 2, async (n) => {
    await after(6 - n); // finish in reverse order
    return n * 10;
  });

  assert.deepEqual(
    results.map((r) => (r.status === "fulfilled" ? r.value : r.status)),
    [10, 20, 30, 40, 50],
  );
});

test("never exceeds the concurrency limit", async () => {
  let running = 0;
  let peak = 0;

  await mapWithConcurrency(Array.from({ length: 9 }), 2, async () => {
    running++;
    peak = Math.max(peak, running);
    await after(2);
    running--;
  });

  assert.equal(peak, 2);
});

test("backfills a freed slot from the queue instead of idling it", async () => {
  // One long pipeline beside a short one, with a deeper queue behind them.
  const started: number[] = [];

  await mapWithConcurrency([9, 1, 1, 1], 2, async (ticks, index) => {
    started.push(index);
    await after(ticks);
  });

  // Item 0 is still running throughout; the single freed slot must work
  // through 1, 2 and 3 rather than waiting for item 0 to finish.
  assert.deepEqual(started, [0, 1, 2, 3]);
});

test("a failing item does not stop the queue", async () => {
  const started: number[] = [];

  const results = await mapWithConcurrency([0, 1, 2, 3], 1, async (_item, index) => {
    started.push(index);
    if (index === 0) throw new Error("transient sandbox crash");
    return index;
  });

  assert.deepEqual(started, [0, 1, 2, 3]);
  assert.equal(results[0]?.status, "rejected");
  assert.equal(results[3]?.status, "fulfilled");
});

test("a failing item never cancels a sibling already in flight", async () => {
  const finished: number[] = [];

  const results = await mapWithConcurrency([0, 1, 2, 3], 2, async (_item, index) => {
    if (index === 0) throw new Error("implementer died");
    await after(3);
    finished.push(index);
    return index;
  });

  // Killing an in-flight pipeline would strand an implementer mid-gates with
  // its tree uncommitted — never do that.
  assert.deepEqual(finished, [1, 2, 3]);
  assert.equal(results[0]?.status, "rejected");
  assert.equal(results[1]?.status, "fulfilled");
});

test("records the rejection reason verbatim", async () => {
  const results = await mapWithConcurrency([0], 1, async () => {
    throw new Error("sandbox crash");
  });

  const outcome = results[0];
  assert.ok(outcome?.status === "rejected");
  assert.equal((outcome.reason as Error).message, "sandbox crash");
});

test("an empty queue starts no workers", async () => {
  const results = await mapWithConcurrency([], 4, async () => {
    throw new Error("must never run");
  });

  assert.deepEqual(results, []);
});
