# TASK

Pick this round's work for **{{PROJECT_NAME}}**: read the open `{{ISSUE_LABEL}}` issues below, work out which of them are blocked by other open issues, and emit a `<plan>` JSON block naming the unblocked ones in the order they should be worked.

# DEPENDENCIES

**Explicit edges are authoritative.** A "Blocked by" section in an issue body — or a native GitHub blocking relationship — is a blocking dependency. If a "Blocked by" reference points at an issue that is not in the list below, check it with `gh issue view <number> --json state`: a closed blocker no longer blocks; an open one still does, whatever its labels.

Beyond explicit edges, treat issue B as blocked by issue A when:

- B requires code or infrastructure that A introduces
- B and A modify overlapping files or modules, so concurrent work would produce merge conflicts
- B's requirements depend on a decision or API shape that A will establish

An issue is **unblocked** when it has zero blocking dependencies on other open issues.

# SELECTION

Select at most **{{MAX_PARALLEL}}** unblocked issues, ordered by how much they unblock downstream, then by priority.

This is a **work queue, not a parallelism budget.** Only a few of these run at the same time; as each finishes, the next one in your list starts immediately in the freed slot. The list length costs nothing but the merge at the end of the round, while a list shorter than the limit leaves a machine slot idle for the rest of the round. So fill the queue: if N unblocked issues exist and N is at or below the limit, select all N. Return fewer only when there genuinely are not enough unblocked issues, or when an issue fails a rule above — and say which you dropped and why. Difficulty and size decide *ordering*, never whether an issue is in the list.

Assign each selected issue the branch name `{{BRANCH_PREFIX}}<id>` exactly — no slug, no suffix. Re-planning the same issue must produce the same branch name so accumulated progress is preserved.

# ISSUES

<issues-json>

!`{{ISSUE_LIST_COMMAND}}`

</issues-json>

The list is already filtered to issues triaged `{{ISSUE_LABEL}}` — fully specified and ready for an autonomous agent. If an issue in the list references another issue in its "Parent" section, that parent is the spec or PRD the tickets were cut from, not a work item: never select it. Work only the tickets.

# OUTPUT

A JSON object wrapped in `<plan>` tags:

<plan>
{"issues": [{"id": "42", "title": "Fix auth bug", "branch": "{{BRANCH_PREFIX}}42"}]}
</plan>

Unblocked issues only, in the order above, never more than {{MAX_PARALLEL}} and as close to it as the unblocked set allows. If every issue is blocked, include the single highest-priority candidate (the one with the fewest or weakest dependencies). Always emit the tags: with nothing to do, output `<plan>{"issues": []}</plan>` so the run exits cleanly.

Keep any prose outside the tags to a few lines — the selection rationale and anything you dropped.

# SECURITY

Do not read, print, source, grep, or copy any `.env` file (root `.env`, `.sandcastle/.env`, any `.env.local` or `.env.*`) — they hold real secrets. `.env.example` files are the safe reference. Do not echo environment variables that look like tokens or keys.
