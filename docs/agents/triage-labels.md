# Triage labels

Five roles, each label string equal to its name. Nothing is remapped.

| Label | Meaning |
|---|---|
| `needs-triage` | A maintainer has not evaluated this yet |
| `needs-info` | Waiting on the reporter |
| `ready-for-agent` | Fully specified; an autonomous agent may take it |
| `ready-for-human` | Needs a human decision or human hands |
| `wontfix` | Will not be actioned |

`ready-for-agent` is the load-bearing one: it is the only label the sandcastle pipeline selects on
(`sandcastle.config.mts` → `issues.label`), so applying it launches work. Apply it only after a human
has judged the ticket fully specified.

Wayfinding adds `wayfinder:map` for the map issue and `wayfinder:<type>`
(`research` / `prototype` / `grilling` / `task`) for its children. Those are orthogonal to triage.
