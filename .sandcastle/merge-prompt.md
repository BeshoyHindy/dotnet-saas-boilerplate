# TASK

Merge these branches into the current branch, resolve any conflicts, gate the result where the rules below require it, and finish with a clean tree:

{{BRANCHES}}

For each branch, run `git merge <branch> --no-edit`. Where it conflicts, read both sides and resolve to the behaviour both branches intended rather than picking a side mechanically.

Before the first merge, record where you started: `BASE=$(git rev-parse HEAD)` — the scoped checks below are relative to it.

# ISSUES — LEAVE THEM OPEN

Do not close any GitHub issue. Your merge lands on the real target branch only after this sandbox finishes; if that final merge-back fails, a prematurely closed issue makes the next planning round skip work that never landed and plan duplicate work against a branch that is missing it. The orchestrator closes issues from the host, once git proves the merge really landed.

For reference, the issues covered by these branches:

{{ISSUES}}

# VALIDATION

Run the gates **only when the merge required real work** — you resolved conflicts, or you merged more than one branch (their combination is untested). A single conflict-free branch merge needs no gates: that exact tree already passed them on its own branch.

{{GATE_COMMANDS}}

Gate only the areas the merged branches touched, and add whatever `.sandcastle/CODING_STANDARDS.md` names for those areas (a client app's lint and build; a browser suite scoped with `--only-changed=$BASE`, never a full one — every branch already ran its own, and CI re-runs them all on the eventual push). Run a client's full suite only if you hand-resolved a conflict in that client's application source, not merely in a spec file.

**This sandbox has no Docker**, so any gate above that needs containers cannot run here. Skip it and say so in your report: the host runs the full set on the merged HEAD immediately after you finish, which is the point of this phase being cheap.

Run gates in the foreground, one at a time, each with an explicit `timeout` of up to 3600000 ms (this sandbox raises the Bash cap to one hour). Ending your turn ends the run: the sandbox is torn down, the gate dies with it, and the merge lands on the real branch ungated — so never end your turn while a gate is in flight. If a gate fails, fix the issue and commit the fix.

# FINISH

Commit everything you did — conflict resolutions, gate fixes, anything. The merge-back to the real branch carries only commits, and any uncommitted change in this worktree is silently discarded (the host detects that, preserves the diff and refuses to continue). After the merges, add a single commit summarising them (skip it if git already created merge commits and there is nothing to add). Then run `git status` and confirm it shows no modifications.

Once you've merged everything you can, output <promise>COMPLETE</promise>.

# SECURITY

Do not read, print, source, grep, or copy any `.env` file (root `.env`, `.sandcastle/.env`, any `.env.local` or `.env.*`) — they hold real secrets. `.env.example` files are the safe reference. Do not echo environment variables that look like tokens or keys, and never `git add` an `.env` file.

# FINAL REPORT

Close with a few lines: which branches merged, which conflicts you resolved and how, which gates you ran and their actual results, which you could not run here, and anything that did not merge.
