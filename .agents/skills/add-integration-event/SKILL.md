---
name: add-integration-event
description: Publish a cross-module integration event via the Outbox and handle it idempotently in another module. Use when one module must react to something that happened in another. See .agents/rules/eventing.md.
argument-hint: "[SourceModule] [EventName] [ConsumerModule]"
---

# Add Integration Event

Cross-module communication goes through **integration events + the Outbox** (transactional, crash-safe) —
never a direct in-process call into another module's runtime, and never `IEventBus.PublishAsync` from a
handler. Full model: `.agents/rules/eventing.md`.

## Step 1 — Define the event (source module's Contracts)

`Modules.{Source}.Contracts/Events/{Event}IntegrationEvent.cs` — implement `IIntegrationEvent`:

```csharp
public sealed record {Event}IntegrationEvent(
    Guid Id,
    DateTime OccurredOnUtc,
    string? TenantId,
    string CorrelationId,
    string Source,
    Guid {Entity}Id,
    string SomePayload) : IIntegrationEvent;
```

⚠️ Don't rename/move this type later — the outbox stores its assembly-qualified name; a rename makes
`Type.GetType()` return null and the message dead-letters. Keep the type name + namespace stable.

## Step 2 — Publish via the Outbox (source handler)

Publishing needs **no module registration** — the outbox is framework-owned and the host wires it once. Inject `IOutboxWriter` (from `Boilerplate.BuildingBlocks.Eventing.Abstractions`) and add the event in the same unit of work:

```csharp
public sealed class Do{Thing}CommandHandler({Source}DbContext db, IOutboxWriter outbox)
    : ICommandHandler<Do{Thing}Command, Unit>
{
    public async ValueTask<Unit> Handle(Do{Thing}Command command, CancellationToken cancellationToken)
    {
        // … mutate entities, db.SaveChangesAsync …
        var evt = new {Event}IntegrationEvent(
            Id: Guid.CreateVersion7(),
            OccurredOnUtc: DateTime.UtcNow,
            TenantId: /* current tenant */,
            CorrelationId: Guid.NewGuid().ToString(),
            Source: "{Source}",
            {Entity}Id: entity.Id,
            SomePayload: "…");
        await outbox.AddAsync(evt, cancellationToken).ConfigureAwait(false);
        return Unit.Value;
    }
}
```

The `OutboxDispatcherHostedService` later publishes it via `IEventBus`.

## Step 3 — Handle it (consumer module)

`Modules.{Consumer}/IntegrationEventHandlers/{Event}IntegrationEventHandler.cs` — `sealed`, implement `IIntegrationEventHandler<T>`:

```csharp
public sealed class {Event}IntegrationEventHandler({Consumer}DbContext db)
    : IIntegrationEventHandler<{Event}IntegrationEvent>
{
    public async Task HandleAsync({Event}IntegrationEvent @event, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(@event);
        // … write to the consumer's tables / push a notification …
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
```

Register the consumer's handlers in its `ConfigureServices`:

```csharp
builder.Services.AddIntegrationEventHandlers(typeof({Consumer}Module).Assembly);
```

## Gotchas

- **Idempotency is free** with the in-memory bus (the Inbox dedups by `{eventId, handlerName}`) — don't hand-roll it.
- The in-memory bus runs handlers **synchronously in the publisher's scope** — keep the handler lean; a throw surfaces to the originating request. Published via the outbox, that scope belongs to the dispatcher, so the consumer runs on the next cycle and its failures never reach the caller. Don't let a caller (or a test) assume the side effect already happened; integration tests drain with `OutboxDrain.DrainAsync`.
- **The tenant is already there.** `IEventTenantScope` installs the event's tenant — the full record from the store, connection string included — before the handlers' DI scope exists, so a handler just resolves its tenant-filtered DbContext. Never write `IMultiTenantContextSetter`; an architecture test fails the build on it.
- **A tenant-less event must say so.** Publishing with a blank `TenantId` throws on dispatch unless the event type implements `IGlobalIntegrationEvent` (the event-side `[SystemJob]`). A global handler that needs tenant data enters each tenant via `ITenantScope`.
- **Module load order:** the consumer must load before the publisher if it must react (`Order` in `[assembly: AppModule]`).

## Checklist

- [ ] Event in source Contracts, implements `IIntegrationEvent`, stable type name
- [ ] Published via `IOutboxWriter.AddAsync` (not the bus); no per-module eventing registration needed
- [ ] Consumer handler `sealed : IIntegrationEventHandler<T>`; `AddIntegrationEventHandlers(assembly)` registered
- [ ] `TenantId` set from the publishing tenant, or the event declared `IGlobalIntegrationEvent`; module `Order` lets the consumer load first
- [ ] Build + tests green
