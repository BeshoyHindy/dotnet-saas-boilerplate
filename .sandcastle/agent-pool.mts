// The issue worker pool — how many pipelines run at once.
//
// Extracted from main.mts so the backfill rule is testable without Docker, a
// model, or a real git repo.
//
// The pool runs at most `limit` pipelines concurrently and pulls the next
// queued item as each slot frees. That backfill is the point: the planner
// queues MORE issues than can run at once (`limits.plannerQueueDepth`), so a
// pipeline that finishes early starts the next issue instead of idling its
// sandbox until its siblings are done.
//
// A failing item is never allowed to cancel the others: an implementer killed
// mid-gates leaves its tree uncommitted, and stranding validated work is the
// failure mode this whole area is built to avoid (see issue-pipeline.mts).

/**
 * Run `fn` over `items`, at most `limit` at a time, backfilling each freed slot
 * from the queue.
 *
 * Returns PromiseSettledResult[] in item order (same shape as
 * Promise.allSettled), so one failing item never cancels the others and
 * downstream filtering is unchanged.
 */
export async function mapWithConcurrency<T, R>(
  items: readonly T[],
  limit: number,
  fn: (item: T, index: number) => Promise<R>,
): Promise<PromiseSettledResult<R>[]> {
  const results: PromiseSettledResult<R>[] = new Array(items.length);
  let next = 0;

  const workers = Array.from(
    { length: Math.min(limit, items.length) },
    async () => {
      while (next < items.length) {
        const i = next++;
        try {
          results[i] = { status: "fulfilled", value: await fn(items[i]!, i) };
        } catch (reason) {
          results[i] = { status: "rejected", reason };
        }
      }
    },
  );

  await Promise.all(workers);

  return results;
}
