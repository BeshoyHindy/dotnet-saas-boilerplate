# TASK

Review the changes on branch `{{BRANCH}}` for issue {{TASK_ID}} ({{ISSUE_TITLE}}): first that they deliver what the issue asked for, then that they are clear, consistent and maintainable. The deliverable is a small set of committed fixes and refinements, or the finding that the branch is already complete and clean.

Scope is the branch diff below. Don't refactor code the branch didn't touch, don't add features or abstractions the issue didn't ask for, and don't alter behaviour the issue did not call for: every original feature, output and behaviour stays intact.

# THE ISSUE — WALK THE ACCEPTANCE CRITERIA FIRST

Read the issue with `gh issue view {{TASK_ID}} --comments` before reading the diff. Take every acceptance criterion (the checkboxes, and the concrete numbers or scenarios the body names) and check it against the branch: the code path that satisfies it, and the test that pins it. Then compare the implementer's commit body, which claims what landed and which gates ran, against what the diff actually contains.

A criterion the branch does not meet, a claimed test that does not exist or asserts something weaker than the claim, a named scenario with no test, or a "decision" the commit body records that contradicts the issue's "do not re-open" list is a **finding to fix on this branch**, not a note. The clarity work below comes after that, never instead of it. When a criterion genuinely cannot be met here (it needs a human, another module, or infrastructure the sandbox lacks), leave a comment on the issue saying exactly which one and why, and say so in your report.

# CONTEXT

## Branch diffstat (every changed file, including generated artifacts)

!`git diff {{TARGET_BRANCH}}...{{BRANCH}} --stat`

## Branch diff

Generated artifacts are excluded below to keep the prompt within the context window — they appear in the diffstat above. Inspect them with `git diff` / `git show` on demand if the change warrants it.

!`git diff {{TARGET_BRANCH}}...{{BRANCH}} -- ':(exclude)**/openapi/*.json' ':(exclude)**/api-schema.d.ts' ':(exclude)*package-lock.json' ':(exclude)*pnpm-lock.yaml'`

## Commits on this branch

!`git log {{TARGET_BRANCH}}..{{BRANCH}} --oneline`

# WHAT TO LOOK FOR

**Correctness** — does the implementation match the intent of the commits and the issue? Are edge cases handled? Are the new or changed behaviours covered by tests? Any unsafe casts, `any` types, unchecked assumptions, injection vulnerabilities or leaked credentials? Report everything you find with its severity, including the low-confidence ones.

**Clarity** — unnecessary complexity or nesting, redundant code and abstractions, unclear names, related logic that belongs together, comments restating what the code already says, nested ternaries where a switch or if/else chain reads better. Prefer explicit code over clever compression, and keep abstractions that genuinely organise the code.

**Project standards** — the coding standards in @.sandcastle/CODING_STANDARDS.md and the area rules it points at.

# EXECUTION

If the branch is already clean and well-structured, change nothing and skip the gates entirely — the implementer already ran them on this branch, and re-running them on an unchanged tree proves nothing.

Otherwise: make the changes on this branch, run targeted checks on what you changed (single test project, single spec file), and **commit the refinements before starting any long-running gate**, so the work survives if the run is cut off. Then run the gates for the areas the branch touches:

{{GATE_COMMANDS}}

For a browser suite, run `--only-changed=<the commit you started from>` (record it with `git rev-parse HEAD` before editing) rather than the full suite: the implementer already ran the scoped suite here, and a full one is tens of minutes. If a gate fails, fix it and commit again.

Run gates in the foreground with a `timeout` of up to 3600000 ms. Ending your turn ends the run and tears the sandbox down, killing any gate with it — so never end your turn while one is in flight.

Once complete, output <promise>COMPLETE</promise>.

# SECURITY

Do not read, print, source, grep, or copy any `.env` file (root `.env`, `.sandcastle/.env`, any `.env.local` or `.env.*`) — they hold real secrets. `.env.example` files are the safe reference. Do not echo environment variables that look like tokens or keys, and never `git add` an `.env` file.

# FINAL REPORT

Close with a short report for someone who did not watch the run: each acceptance criterion with met / fixed here / not met and why, what else you changed and why, anything you found but deliberately left alone, and the gate results — stated as they actually came out.
