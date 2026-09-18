// Durable close ledger — "issues to close once their recorded work is in HEAD".
//
// Closing merged issues runs in-process, and three real paths skip it with the
// merge already landed: the orchestrator dies before the merger's catch runs
// (SIGKILL, power loss), `gh` fails mid-close-loop (network, expired auth), or a
// human merges a preserved temp branch by hand and reruns without closing —
// which once re-planned a whole round of already-merged issues as duplicates.
// main.mts writes one entry per completed branch BEFORE the merger runs and
// reconciles the ledger at every startup: entries whose recorded TIP is
// provably in HEAD get their issues closed and leave the ledger; the rest
// wait. Judging the recorded tip — never the branch ref — means a branch that
// gains new commits after the round cannot close its issue on the strength of
// old work.
//
// Pure core (issue-pipeline.mts precedent): file IO and git/gh live in
// main.mts and arrive here as data + callbacks, so the rules are testable
// without Docker, a model, or a real repo.

export interface PendingClose {
  id: string;
  branch: string;
  tip: string;
}

/**
 * Parses ledger file content defensively: a corrupt or mis-shaped ledger must
 * never block a run, so unusable entries are dropped — the worst case is the
 * pre-ledger behavior (an open issue a human closes by hand).
 */
export function parseLedger(raw: string | null): PendingClose[] {
  if (raw === null || raw.trim().length === 0) {
    return [];
  }
  let parsed: unknown;
  try {
    parsed = JSON.parse(raw);
  } catch {
    return [];
  }
  if (!Array.isArray(parsed)) {
    return [];
  }
  return parsed.filter((entry): entry is PendingClose => {
    if (typeof entry !== "object" || entry === null) {
      return false;
    }
    const candidate = entry as Record<string, unknown>;
    return typeof candidate.id === "string" && candidate.id.length > 0
      && typeof candidate.branch === "string"
      && typeof candidate.tip === "string" && candidate.tip.length > 0;
  });
}

export function serializeLedger(entries: readonly PendingClose[]): string {
  return `${JSON.stringify(entries, null, 2)}\n`;
}

/**
 * The pre-merger write: the incoming round's entries supersede same-id
 * entries, and every OTHER existing entry survives. A blind overwrite here
 * would silently drop still-waiting entries from earlier rounds — losing
 * exactly the durable record the ledger exists to keep.
 */
export function mergeLedger(
  existing: readonly PendingClose[],
  incoming: readonly PendingClose[],
): PendingClose[] {
  const incomingIds = new Set(incoming.map((entry) => entry.id));
  return [...existing.filter((entry) => !incomingIds.has(entry.id)), ...incoming];
}

/**
 * Splits the ledger into entries whose recorded tip is in HEAD (close their
 * issues now) and entries still waiting for their work to land.
 */
export function partitionLedger(
  entries: readonly PendingClose[],
  commitLandedInHead: (tip: string) => boolean,
): { landed: PendingClose[]; waiting: PendingClose[] } {
  const landed: PendingClose[] = [];
  const waiting: PendingClose[] = [];
  for (const entry of entries) {
    (commitLandedInHead(entry.tip) ? landed : waiting).push(entry);
  }
  return { landed, waiting };
}

/**
 * The ledger after a close pass: an entry leaves only when its issue was
 * actually closed (or observed already closed). A landed tip whose close
 * FAILED stays recorded, so the next startup retries instead of forgetting —
 * forgetting is exactly the duplicate-round hole this ledger exists to plug.
 */
export function remainingLedger(
  entries: readonly PendingClose[],
  closedIssueIds: ReadonlySet<string>,
): PendingClose[] {
  return entries.filter((entry) => !closedIssueIds.has(entry.id));
}
