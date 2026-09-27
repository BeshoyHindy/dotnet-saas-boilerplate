using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Boilerplate.BuildingBlocks.Eventing;
using Boilerplate.BuildingBlocks.Eventing.Abstractions;
using Boilerplate.BuildingBlocks.Eventing.Outbox;
using Boilerplate.BuildingBlocks.Eventing.Serialization;
using Boilerplate.BuildingBlocks.Eventing.Telemetry;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Framework.Tests.Eventing;

/// <summary>
/// Outbox dispatch is observable: a span per message tagged with what an operator searches by, a
/// counter of messages dispatched, and a gauge of the rows still waiting. Shares a collection with
/// the other dispatcher tests because the pending gauge is process-wide.
/// </summary>
[Collection(OutboxDispatcherDefinition.Name)]
public sealed class OutboxDispatcherTelemetryTests
{
    public sealed record TelemetryProbeEvent : IIntegrationEvent
    {
        public Guid Id { get; init; } = Guid.CreateVersion7();
        public DateTime OccurredOnUtc { get; init; } = DateTime.UtcNow;
        public string? TenantId { get; init; }
        public string CorrelationId { get; init; } = string.Empty;
        public string Source { get; init; } = "tests";
    }

    [Fact]
    public async Task Each_Dispatched_Message_Gets_A_Span_Tagged_With_Type_Tenant_And_Correlation()
    {
        var first = NewMessage("tenant-a", "0af7651916cd43dd8448eb211c80319c");
        var second = NewMessage("tenant-b", "4bf92f3577b34da6a3ce929d0e0e4736");
        var ids = new HashSet<Guid> { first.Id, second.Id };

        var spans = new ConcurrentBag<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == EventingTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a =>
            {
                if (a.GetTagItem("messaging.message.id") is Guid id && ids.Contains(id))
                {
                    spans.Add(a);
                }
            },
        };
        ActivitySource.AddActivityListener(listener);

        await CreateSut(StoreReturning(pending: 0, first, second)).DispatchAsync();

        spans.Count.ShouldBe(2);
        var span = spans.Single(a => (Guid)a.GetTagItem("messaging.message.id")! == first.Id);
        span.GetTagItem("boilerplate.event_type").ShouldBe(typeof(TelemetryProbeEvent).FullName);
        span.GetTagItem("boilerplate.tenant_id").ShouldBe("tenant-a");
        span.GetTagItem("boilerplate.correlation_id").ShouldBe("0af7651916cd43dd8448eb211c80319c");
        span.Status.ShouldBe(ActivityStatusCode.Ok);
    }

    [Fact]
    public async Task Dispatching_Counts_Each_Processed_Message_And_Samples_The_Pending_Backlog()
    {
        long dispatched = 0;
        long? pending = null;

        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == EventingTelemetry.MeterName)
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, _, _) =>
        {
            switch (instrument.Name)
            {
                case "boilerplate.eventing.outbox.dispatched":
                    Interlocked.Add(ref dispatched, value);
                    break;
                case "boilerplate.eventing.outbox.pending":
                    pending = value;
                    break;
            }
        });
        listener.Start();

        var store = StoreReturning(pending: 7, NewMessage("tenant-a", "c1"), NewMessage("tenant-a", "c2"));
        await CreateSut(store).DispatchAsync();

        Interlocked.Read(ref dispatched).ShouldBe(2);

        listener.RecordObservableInstruments();
        pending.ShouldBe(7, "the gauge reports the backlog the store counted after the pass");
    }

    [Fact]
    public async Task A_Failing_Pending_Count_Does_Not_Fail_The_Dispatch()
    {
        var message = NewMessage("tenant-a", "c1");
        var store = StoreReturning(pending: 0, message);
        store.CountPendingAsync(Arg.Any<CancellationToken>())
            .Returns<Task<int>>(_ => throw new InvalidOperationException("count failed"));

        await CreateSut(store).DispatchAsync();

        await store.Received(1).MarkAsProcessedAsync(message, Arg.Any<CancellationToken>());
    }

    private static OutboxMessage NewMessage(string tenantId, string correlationId)
    {
        var @event = new TelemetryProbeEvent { TenantId = tenantId, CorrelationId = correlationId };
        return new OutboxMessage
        {
            Id = @event.Id,
            CreatedOnUtc = @event.OccurredOnUtc,
            Type = typeof(TelemetryProbeEvent).AssemblyQualifiedName!,
            Payload = new JsonEventSerializer().Serialize(@event),
            TenantId = tenantId,
            CorrelationId = correlationId,
        };
    }

    private static IOutboxStore StoreReturning(int pending, params OutboxMessage[] messages)
    {
        var store = Substitute.For<IOutboxStore>();
        store.ClaimBatchAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<OutboxMessage>>(messages));
        store.CountPendingAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(pending));
        return store;
    }

    private static OutboxDispatcher CreateSut(IOutboxStore store) =>
        new(store,
            Substitute.For<IEventBus>(),
            new JsonEventSerializer(),
            Options.Create(new EventingOptions()),
            NullLogger<OutboxDispatcher>.Instance);
}

/// <summary>
/// Dispatcher tests write the process-wide pending gauge, so they run one at a time.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class OutboxDispatcherDefinition
{
    public const string Name = "OutboxDispatcher";

    private OutboxDispatcherDefinition()
    {
    }
}
