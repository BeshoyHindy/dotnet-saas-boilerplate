---
status: accepted
---
# `Boilerplate.*` placeholder namespace, renamed by `dotnet new` only

The template ships under the neutral root name `Boilerplate` (solution `Boilerplate.slnx`, projects `Boilerplate.Api`, `Boilerplate.BuildingBlocks.*`, `Boilerplate.Modules.*`). A new product gets its own name with one command: `dotnet new install <repo>` then `dotnet new saas -n Acme`. The repo root *is* the template (`.template.config/template.json`, `sourceName: "Boilerplate"`), with derived symbols for the lowercase (`boilerplate` → `acme`: Docker images, npm package names, compose project, cache prefixes) and kebab forms.

We ship exactly one rename mechanism. A rename script was rejected: it duplicates what the template engine already does across every file type (C#, JSON, YAML, TS, Markdown), and two mechanisms drift. `Boilerplate` was chosen over `App`/`Saas` because it does not collide with real identifiers (`AppHost`, `AppSettings`), so text replacement is safe.

## Consequences

- The word "Boilerplate" is reserved: prose in docs and comments must not use it as a common noun, or it will be rewritten to the product name. CI enforces this with a template-smoke job: scaffold `-n Acme`, build with warnings as errors, run architecture tests, and assert `grep -ri boilerplate` is empty in the output.
- The upstream MIT copyright line stays in `LICENSE` (legally required) and is the only permitted upstream reference; a brand-grep CI gate enforces that.
