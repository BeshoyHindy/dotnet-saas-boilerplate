using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Boilerplate.BuildingBlocks.Eventing.Telemetry;

/// <summary>
/// OpenTelemetry primitives for the eventing building block. Register with
/// <c>metrics.AddMeter(EventingTelemetry.MeterName)</c> and
/// <c>tracing.AddSource(EventingTelemetry.ActivitySourceName)</c> in the OTel setup.
/// </summary>
public static class EventingTelemetry
{
    /// <summary>Name of the <see cref="ActivitySource"/> used for outbox dispatch spans.</summary>
    public const string ActivitySourceName = "Boilerplate.Eventing";

    /// <summary>Name of the <see cref="Meter"/> used for eventing metrics.</summary>
    public const string MeterName = "Boilerplate.Eventing";

    /// <summary>
    /// Span tag carrying the integration event's correlation id — the same tag the request span
    /// carries, so a dispatch can be joined back to the request that published it.
    /// </summary>
    internal const string CorrelationIdTag = "boilerplate.correlation_id";

    internal static readonly ActivitySource ActivitySource = new(ActivitySourceName);
    internal static readonly Meter Meter = new(MeterName);

    /// <summary>Outbox messages published to the bus and marked processed.</summary>
    internal static readonly Counter<long> OutboxDispatched = Meter.CreateCounter<long>(
        "boilerplate.eventing.outbox.dispatched",
        unit: "{message}",
        description: "Number of outbox messages published and marked as processed.");

    /// <summary>Outbox messages that exhausted their retries and were dead-lettered.</summary>
    internal static readonly Counter<long> OutboxDeadLettered = Meter.CreateCounter<long>(
        "boilerplate.eventing.outbox.deadlettered",
        unit: "{message}",
        description: "Number of outbox messages moved to dead-letter after exhausting retries.");

    /// <summary>Dead-lettered outbox messages reset for another dispatch attempt.</summary>
    internal static readonly Counter<long> OutboxRedriven = Meter.CreateCounter<long>(
        "boilerplate.eventing.outbox.redriven",
        unit: "{message}",
        description: "Number of dead-lettered outbox messages redriven for another attempt.");

    private static long _outboxPending;

    /// <summary>
    /// Outbox rows not yet processed and not dead-lettered, as of the last dispatch pass. A value
    /// that keeps rising is a backlog building up before anything is dead-lettered.
    /// </summary>
    internal static readonly ObservableGauge<long> OutboxPending = Meter.CreateObservableGauge(
        "boilerplate.eventing.outbox.pending",
        () => Volatile.Read(ref _outboxPending),
        unit: "{message}",
        description: "Outbox messages waiting to be dispatched (not processed, not dead-lettered), sampled each dispatch pass.");

    /// <summary>Records the pending-row count the <see cref="OutboxPending"/> gauge reports.</summary>
    internal static void RecordOutboxPending(long count) => Volatile.Write(ref _outboxPending, count);
}
