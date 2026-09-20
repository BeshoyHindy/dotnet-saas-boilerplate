# Eventing — domain events, integration events, Outbox/Inbox

Read before publishing/handling cross-module events. `src/BuildingBlocks/Eventing/`.

## Two tiers

- **Domain events** (in-process, pre-commit) — inherit `DomainEvent` (record: `EventId`, `OccurredOnUtc`, `CorrelationId`, `TenantId`). Raised on aggregates (`IHasDomainEvents`).
- **Integration events** (cross-module, async) — implement `IIntegrationEvent` (`Id`, `OccurredOnUtc`, `TenantId`, `CorrelationId`, `Source`). Handlers implement `IIntegrationEventHandler<T>` (single `HandleAsync(T, ct)`), are `sealed`, live in `Events/` or `IntegrationEventHandlers/`.

Every integration event is published under a tenant (ADR-0002). An event published with a blank `TenantId` **throws on dispatch** unless its type implements `IGlobalIntegrationEvent` — the event-side `[SystemJob]`. A null `TenantId` used to mean both "platform-wide" and "somebody forgot", and nothing downstream could tell the two apart; declare the first, and the second stays a bug that fails loudly. A global handler that needs tenant data enters each tenant through `ITenantScope`.

## The Outbox is the only way to publish

**Do not call `IEventBus` directly from a handler.** Publish via the outbox so the event commits with the business write and survives a crash:

```csharp
await _outbox.AddAsync(integrationEvent, ct).ConfigureAwait(false);   // IOutboxWriter
```

Inject **`IOutboxWriter`** (`Boilerplate.BuildingBlocks.Eventing.Abstractions`) — the publish-side contract, and all a module ever needs. `IOutboxStore` is the full dispatcher-side surface and lives in the eventing runtime, which modules don't reference.

`EfCoreOutboxStore.AddAsync` serializes + `SaveChanges` immediately, joining the caller's transaction when there is one. `OutboxDispatcherHostedService` polls every `OutboxDispatchIntervalSeconds` (default 10), `OutboxDispatcher` **claims** a batch (`OutboxBatchSize`, default 100), publishes via `IEventBus`, and dead-letters after `OutboxMaxRetries` (default 5) → `IsDead`. Failures back off exponentially (`NextRetryAt`); `RedriveDeadLettersAsync` recovers dead rows. `OutboxMessage`/`InboxMessage` are `IGlobalEntity` (no tenant filter — the dispatcher has no tenant context; `TenantId` is an explicit column).

**Publishing is asynchronous.** The consumer runs on the next dispatch cycle, not inside the request. Don't write a caller — or a test — that assumes the side effect already happened. Integration tests drain explicitly via `OutboxDrain.DrainAsync`.

Any exception to publishing via the outbox needs a strong reason, documented in a comment at the call site.

## One store, owned by the framework

`OutboxMessages`/`InboxMessages` live in schema `framework`, owned by `EventingDbContext` (`src/BuildingBlocks/Eventing/Persistence/`) — **not** by any module's context. That is what keeps `IOutboxStore`/`IInboxStore` to a single, non-keyed DI registration: registering them per module DbContext made .NET DI resolve whichever module registered last for the whole application, so a second module publishing broke every module's outbox (issue #1349). `EventingRegistrationTests` guards the registration count; don't add a second one.

`EventingDbContext` derives from `BaseDbContext`, so its rows sit in the one shared database next to the business data they accompany, and one dispatcher pass per cycle sees every tenant's rows.

## Multi-instance safety

`ClaimBatchAsync` leases rows with `FOR UPDATE SKIP LOCKED` in a single `UPDATE … RETURNING`, so several API instances partition a batch instead of all publishing the same message. `ClaimedUntilUtc` is an expiry (`OutboxClaimLeaseSeconds`, default 300), so a dispatcher that dies mid-batch has its rows recovered rather than stranded — raise it if a batch can take longer than the lease, or a second instance re-claims rows still in flight. Completing or failing a message releases the lease. Non-Postgres providers have no portable `SKIP LOCKED`: they fall back to an unclaimed read and log a warning that only one instance is safe.

## Atomicity

`IScopedDbConnectionProvider` gives every DbContext in a DI scope the same `DbConnection`, which is the only way EF Core can enlist a second context in a transaction another one opened (Npgsql has no distributed-transaction promotion). `AmbientDbTransactionRegistry` — an `IDbTransactionInterceptor` on every Hero context — records open transactions, since `DbConnection` can't be asked. `AddAsync` joins the ambient transaction when there is one, so the outbox row commits or rolls back with the business data; with none, it commits on its own exactly as before.

## Idempotency is free (in-memory bus)

`InMemoryEventBus` resolves handlers in a fresh DI scope and applies the **Inbox**: skips if `IInboxStore.HasProcessedAsync(eventId, handlerName)`, marks processed after success. Composite key `{Id, HandlerName}`; concurrent-insert race is swallowed. Don't hand-roll dedup.

## Wiring

The **host** bootstraps eventing once (`Boilerplate.Api/Program.cs` and `Boilerplate.DbMigrator/Program.cs`, before `AddModules` so `EventingDbInitializer` migrates the `framework` schema first):

```csharp
builder.Services.AddEventingCore(builder.Configuration);   // serializer + bus + dispatcher + EventingDbContext + stores
```

A **module** only registers its handlers:

```csharp
services.AddIntegrationEventHandlers(typeof(MyModule).Assembly);        // scans IIntegrationEventHandler<>
```

There is no per-module store registration — `AddEventingForDbContext<T>` was removed in #1349. A module publishes by injecting `IOutboxWriter`; nothing else is needed.

Bus = `InMemoryEventBus`, always. ADR-0003 dropped the RabbitMQ provider (and `EventingOptions.Provider` with it): the monolith runs handlers in-process and the outbox/inbox pair provides the durability a broker would.

## Gotchas

- **Renaming/moving an integration event type breaks deserialization** — the outbox stores the assembly-qualified type name; `Type.GetType()` returns null → the message dead-letters. Keep event type names/namespaces stable, or migrate dead rows.
- **Handlers never restore the tenant themselves.** `IEventTenantScope.DispatchAsync` does it: it loads the event's tenant as a full record from the store (cache-first through `ITenantScope`), installs it, and hands the bus the DI scope the handlers are resolved from — so a handler's DbContext is built with the right tenant already ambient, and an unknown or deactivated tenant fails the dispatch closed. Writing `IMultiTenantContextSetter` in a handler is an architecture test failure.
- In-memory bus runs handlers **synchronously in the publisher's scope** — keep handler work minimal; exceptions surface to the originating request (relevant for Notifications consuming other modules' events). Via the outbox that scope is the dispatcher's, not the request's.
- Set `UseHostedServiceDispatcher=false` to drive the outbox via Hangfire instead of the hosted service.
- A background publisher must publish **inside** `ITenantScope.RunAsync`, resolving `IOutboxWriter` from that scope — otherwise the outbox row is stamped with whatever tenant happened to be ambient (usually none), and the handler is later dispatched under the wrong one or throws for a blank `TenantId` (see `TenantExpiryScanJob`).
