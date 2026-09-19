# Domain docs

Single-context repo: one [`CONTEXT.md`](../../CONTEXT.md) at the root, one `docs/adr/` beside it.
There is no `CONTEXT-MAP.md` and there are no per-module ADR folders — the five modules are one
bounded context, not five.

## Read before exploring

- **`CONTEXT.md`** — the glossary. It defines the words and, as importantly, the words to avoid.
- **`docs/adr/`** — the accepted decisions. Read the ones touching the area you are about to change.
  The tenancy invariant is ADR-0002; it constrains almost everything.

## Use the glossary's vocabulary

When your output names a domain concept — an issue title, a type name, a test name, a commit subject
— use the term `CONTEXT.md` defines, and none of the synonyms it lists under `_Avoid_`. "Switch
tenant", "superadmin" and "the clients" all name things this repo deliberately does not have.

A concept missing from the glossary is a signal: either you are inventing language the project does
not use, or there is a real gap worth recording.

## Flag conflicts, don't route around them

If a change contradicts an ADR, say so in the PR or the ticket rather than silently overriding it —
ADRs are how this repo's non-obvious constraints survive. Reopening one is cheap; discovering it was
quietly broken is not.

Machine-checked rules live in `.agents/rules/`, and several ADRs are additionally enforced by tests
(module boundaries, tenant strategy, endpoint authorization intent). A rule file and the code it
describes are expected to agree; when they do not, the code is right and the rule file is a bug.
