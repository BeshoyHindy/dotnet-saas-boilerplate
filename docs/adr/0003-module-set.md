---
status: accepted
---
# Core module set: Identity, Multitenancy, Auditing, Files, Notifications

The template ships five modules. Identity and Multitenancy are the product's spine; Auditing is cross-cutting and expensive to retrofit; Files exercises storage, presigned uploads and quota-free tenant isolation; Notifications is kept deliberately thin as the one in-repo consumer of integration events, so the outbox/inbox path is exercised by real code and tests.

Removed: the Catalog, Chat and Tickets sample modules; Billing (plans, invoices, wallets, PDF rendering) and the Quota building block that depends on its plan catalog; Webhooks. Billing is the hard call: every SaaS bills, but every SaaS bills differently (provider, pricing model, tax), and the upstream module also forms a dependency cycle with Multitenancy. A template that ships an opinionated ledger ships something most products delete. Tenant validity (`ValidUpto`, activation) stays in Multitenancy as the seam a future billing module drives.

The reachability rule governs everything else: a building block, package or feature survives only if a kept module or the host references it after the removals. Expected casualties: the RabbitMQ bus provider (the in-memory bus plus outbox/inbox is the monolith's bus), SSE (SignalR is kept only if Notifications pushes through it), SQL Server and other non-PostgreSQL providers, Terraform/AWS infrastructure, the upstream CLI tool, and the demo seeder.

## Amendment — 2026-09-19: demo account seeding restored

Deleting the demo seeder went one step too far. With only the root tenant and its single admin user, a fresh stack has nothing to sign in as and no second tenant to look at, and a template that cannot be evaluated is not a template. Demo seeding is back as `src/Host/Boilerplate.DbMigrator/DemoSeed/`, trimmed to the kept modules: the `acme` and `globex` tenants, their people, two custom roles and a few groups — and nothing for the removed modules (no plans, invoices, catalog, tickets, chat, webhooks or quota). No removed module or package returns with it.

What keeps this consistent with the decision above: it is opt-in (`apply --demo`), it is refused outright when the environment is Production, its shared password is configuration (`Seed:DemoPassword`) with no literal in the repository, and it is one directory a new product deletes in one commit. The five-module set is unchanged.
