# TASK

Fix issue {{TASK_ID}} in **{{PROJECT_NAME}}**: {{ISSUE_TITLE}}

The finished state is: the issue implemented on branch {{BRANCH}}, covered by tests, committed, with the validation gates below run and passing.

Read the issue first with `gh issue view {{TASK_ID}} --comments`. If its body has a "Parent" section referencing a spec or PRD issue, read that too — it holds the full context the ticket was cut from.

Work only this issue, at the scope it intends. Make routine judgment calls yourself; if you conclude the ticket is mistaken or a better approach exists, say so in the commit body and deliver the ticket as written. Don't widen the change into adjacent refactors, abstractions, or error handling for cases that cannot happen.

# AUTONOMOUS RUN

You are operating autonomously: nobody is watching and nobody can answer a question mid-task, so asking "shall I…?" only blocks the work. For reversible actions that follow from the issue, proceed.

Ending your turn ends the run and tears the sandbox down, so never end it to "wait for" a command — and before you end it, re-read your last message: if it is a plan, a question, or a promise about work you have not done, do that work now with tool calls. End the turn only when the issue is complete or you are blocked on something only a human can provide.

# CONTEXT

Read `.sandcastle/CODING_STANDARDS.md` and the area rules it points at before you write code.

The last 3 commits in full (their bodies carry decisions and notes for the next iteration):

<recent-commits>

!`git log -n 3 --format="%H%n%ad%n%B---" --date=short`

</recent-commits>

Older history, subjects only:

<recent-history>

!`git log -n 10 --format="%h %ad %s" --date=short`

</recent-history>

# ENVIRONMENT

This sandbox has **warm shared caches** mounted for the package managers this repo uses (`~/.nuget/packages`, with `NUGET_PACKAGES` preset; `~/.npm`; `~/.pnpm-store` and `~/.cache/pnpm`; `~/.cache/ms-playwright` for browsers). Restores and installs should be fast — if a restore or install downloads the world, something is wrong. A *permission* error on one of these cache paths is an infrastructure bug, not your code: apply `mkdir -p /tmp/dnhome && export HOME=/tmp/dnhome`, note it in the commit body, and move on rather than debugging it.

Run builds and test suites **in the foreground** with an explicit `timeout` of up to 3600000 ms (this sandbox raises the Bash cap to one hour). Backgrounding and polling wastes minutes — browser-suite output through a pipe is buffered until exit.

**There is no Docker in this sandbox.** Any suite that starts containers cannot run here; see *Validation gates* below for what that means for the tests you write.

# APPROACH

Where it fits, drive the change test-first: one failing test, the implementation that passes it, repeat, then refactor. Test files touching the area you are changing are the best guide to the conventions expected of you.

**Edit files with the `Edit` tool (and `Write` for new files) — never with `perl -0pi`, `sed -i`, or `cat <<EOF` heredocs in Bash.** A Bash rewrite retypes the whole old block and the whole new block as your output, and that output is the single most expensive artifact in this run; `Edit` sends only the replacement and fails loudly on a stale match instead of silently rewriting nothing. Reading is fine in Bash (`cat`, `sed -n`, `grep`) but prefer the `Read` tool for a file you are about to edit.

# VALIDATION GATES

While iterating, use targeted checks for speed: a single test project or a `--filter` for the backend, a single spec file for a browser suite.

**Before your final commit, run these gates — the same ones the host runs on the merged result:**

{{GATE_COMMANDS}}

Run them in the foreground, one at a time, each with an explicit `timeout` of up to 3600000 ms. If one fails, fix it and run it again. Anything you touched that these gates do not cover (a client app's lint and build, a scoped browser suite for the feature you changed) you run as well — `.sandcastle/CODING_STANDARDS.md` says which.

**Commit before the long run, not after.** A suite can run silently for 10+ minutes; if the run is killed during it, uncommitted work is lost while committed work survives on the branch. Make your final commit, then run the long gate, then commit any fixes it forces.

**Scope the browser suites.** Never run a client's full end-to-end suite here: several sandboxes share one Docker VM, a full suite takes tens of minutes contended, times out in bulk, and then costs another hour of re-runs to tell flakes from regressions. Run the specs `--only-changed` selects against the branch base plus the folders for the feature areas your source change touches. CI runs every full suite on the eventual push, and that is where they belong.

**A container-backed test you write here is UNVERIFIED.** This sandbox has no Docker, Sandcastle never pushes your branch and never opens a pull request, so nothing on this branch reaches CI: the host's post-merge gate is the first thing that ever runs it, and a wrong assertion there turns the merged HEAD red for the whole round. If you add or change one: pin the same behaviour first at a seam you *can* run here, derive every expected value from code you have read instead of guessing, match JSON on the exact property (`"foo":`, not a bare substring a longer sibling name also matches), walk the guard clauses with your literal numbers to confirm the scenario reaches the branch you are asserting on, and follow `.agents/rules/integration-testing.md` exactly — its rules exist because each one has already cost a red merge.

# COMMIT

**Never run `git worktree` in a sandbox** — not `add`, not `remove`, not `prune`. Your workspace IS a host worktree bind-mounted into the container, sharing the host's `.git`. The host's other worktree paths do not exist inside the container, so `git worktree prune` here deletes the host's metadata for every sibling worktree and breaks every running agent's checkout, including yours. To compare against an older commit use `git show <ref>:<path>`, `git diff <ref> -- <path>`, or `git stash` on your own branch — never a second checkout.

Commit with a message that covers, concisely: the task completed plus its issue reference, key decisions, files changed, and any blockers or notes for the next iteration.

**The body lists every acceptance criterion of the issue with its evidence** — the test name or the command whose output proves it, or the words "NOT MET" and why. Claim only what a test or a gate on this branch actually demonstrated: the reviewer that follows you walks the same list against the diff, and a criterion asserted without evidence is a finding against the branch. A container-backed test you could not run here is "written, UNVERIFIED (no Docker)", never "passing".

# THE ISSUE

If the task is not complete, leave a comment on the issue saying what was done. Do not close the issue — that happens later, from the host, and only once git proves the work landed.

Once complete, output <promise>COMPLETE</promise>.

# SECURITY

Do not read, print, source, grep, or copy any `.env` file (root `.env`, `.sandcastle/.env`, any `.env.local` or `.env.*`) — they hold real secrets. `.env.example` files are the safe reference. Do not echo environment variables that look like tokens or keys, and never `git add` an `.env` file.

# FINAL REPORT

Close with a short report: what landed, which gates you ran and their results, and anything left undone. Report gate outcomes faithfully — if a suite failed or you skipped one, say so plainly with the output rather than summarising it as fine.
