---
status: accepted
---
# A product's own features live in product modules

The template ships five modules (ADR-0003): Identity, Multitenancy, Auditing, Files and
Notifications. They are the **platform modules**. A product built from the template has nouns of its
own, and the first of them needs a home. The docs used to say "five and only five" and that "the
sixth needs a reason", so the first product to try it had no answer. Decided in issue #101.

A product's own nouns go in **product modules**: one per bounded context of the product, not one per
feature. A tenant-scoped `Note` becomes a `Notes` module, and later Note features become slices
inside it. A product module is built the same way as a platform module (a runtime project plus a
`.Contracts` project, `[AppModule]`, the same architecture tests) and follows the same rules.

One rule is added, and it points one way: **a product module may use a platform module's
`.Contracts`; a platform module never references a product module**, runtime or `.Contracts`.
`PlatformModuleDirectionTests` (Architecture.Tests) names the five platform modules and fails when
one of them references any module outside that set. The reason is upstream: a product keeps pulling
fixes from this template, and that only merges cleanly while the platform modules know nothing about
the product. When a platform module needs to react to product behaviour, the dependency is turned
around: the product module calls the platform module's `.Contracts`, or handles an integration event
the platform module publishes.

Product modules take `[AppModule]` order 1000 and up, in steps of 100, so they start after the
platform modules and leave room between them.

## Rejected

- **One `Product` module for everything the product adds.** It is a module named after the
  codebase rather than a bounded context. Every product noun would share one DbContext, one
  permission prefix and one rule file, and the boundary the module rules exist to protect would be
  missing inside it.
- **Growing a platform module** (putting `Note` into Identity or Files because it is close by). The
  product's code would then live in files the template also changes, and every upstream merge
  would conflict there. It also puts product nouns into a module whose rule file describes something
  else.

## Consequences

- Adding a module is a documented path (`docs/new-project-guide.md` §3, *A new product module*)
  with seven edits. The four host lists stay literal, because Mediator's source generator reads
  `o.Assemblies` as written. `HostModuleListTests` fails when an `[AppModule]` assembly is missing
  from any of them, naming the list and the file.
- A product module gets its own `.agents/rules/modules/<name>.md`.
- A `dotnet new` item template that generates a module is not part of this decision. It would be a
  second template to maintain, and it only pays for itself after the first product module.
