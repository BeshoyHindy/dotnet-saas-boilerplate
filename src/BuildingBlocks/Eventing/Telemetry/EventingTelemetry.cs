using System.Diagnostics.Metrics;

namespace Boilerplate.BuildingBlocks.Eventing.Telemetry;

/// <summary>
/// OpenTelemetry metrics for the eventing building block. Register with
/// <c>metrics.AddMeter(EventingTelemetry.MeterName)</c> in the OTel setup.
/// </summary>
public static class EventingTelemetry
{
    /// <summary>Name of the <see cref="Meter"/> used for eventing metrics.</summary>
    public const string MeterName = "Boilerplate.Eventing";

    internal static readonly Meter Meter = new(MeterName);

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
}
