# Issue tracker: GitHub

Issues and specs for this repo live as GitHub issues on this repository. Use the `gh` CLI; it infers
the repo from the checkout's remote, so nothing here names one.

## Conventions

- **Read**: `gh issue view <n> --comments`. A closed ticket's last comment is its resolution — what
  was decided, what landed, and the open facts handed to later tickets. Read it before touching the
  area it covers.
- **Create**: `gh issue create --title "..." --body "..."` (heredoc for multi-line bodies).
- **List**: `gh issue list --state open --json number,title,labels`.
- **Comment / label / close**: `gh issue comment <n> --body "..."`,
  `gh issue edit <n> --add-label "..."` / `--remove-label "..."`, `gh issue close <n>`.

Pull requests are not a triage surface here: they are the delivery mechanism for a ticket, not a
place work is requested.

## Wayfinding

One issue is the **map** (label `wayfinder:map`). Every ticket is a GitHub sub-issue of it, and its
body opens with `Part of #<map>`. The map's *Decisions so far* section is the index: one line per
closed ticket, linking it.

- **Blocking** uses GitHub's native issue dependencies, so a blocker is visible in the UI and in the
  API (`issue_dependencies_summary.blocked_by` counts open blockers only). Add an edge with
  `gh api --method POST repos/{owner}/{repo}/issues/<child>/dependencies/blocked_by -F issue_id=<blocker-database-id>`
  — the numeric database id from `gh api repos/{owner}/{repo}/issues/<n> --jq .id`, not the number.
- **Frontier**: open children of the map, minus any with an open blocker or an assignee, first in map
  order.
- **Claim**: `gh issue edit <n> --add-assignee @me`.
- **Resolve**: comment the resolution, close, then add the one-line entry to the map's
  *Decisions so far*.

The sandcastle pipeline reads the same listing — see `sandcastle.config.mts`.
