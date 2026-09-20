# Module: Notifications

Per-user in-app inbox (bell icon) driven by cross-module integration events. Clients poll it (TanStack Query); ADR-0003 removed the push transport. Module `Order = 750` so its handlers are registered before the publishers whose events it consumes.

**Entities / DbContext:** `Notification` (aggregate: `UserId`, `Type`, `Title`/`Body`/`Link`, `Source`, `MetadataJson`, `ReadAtUtc`). `NotificationsDbContext`. Consumes integration events from other modules.
**Areas:** List, GetUnreadCount, MarkRead, MarkAllRead. Full list: `Features/v1/` or `/scalar`.

## Gotchas

- **It's a consumer.** New notification types come from **handling another module's integration event** (`AddIntegrationEventHandlers`), not from new endpoints. The handler writes an inbox row; there is no push side — the client's bell query picks it up.
- **Order matters** — Notifications (750) must load before any publisher whose events it consumes. If a new module publishes events Notifications should react to, mind the `Order`.
- In-memory bus runs handlers **synchronously in the publisher's request scope** — keep the handler minimal; an exception surfaces to the originating request. See `eventing.md`.
- Inbox rows are **denormalized** (Title/Body/Link/MetadataJson copied in) so rendering never calls back into the source module. `MarkRead` is idempotent (`ReadAtUtc ??= now`).
