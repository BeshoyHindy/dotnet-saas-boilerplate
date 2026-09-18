using System.Diagnostics.Metrics;

namespace Boilerplate.Modules.Auditing;

/// <summary>
/// OpenTelemetry instruments for the auditing pipeline. Exposed as static
/// fields so they're cheap to reference from anywhere on the hot path —
/// no DI lookup, no allocation. Wire into the OTel exporter via
/// <c>metrics.AddMeter(AuditingTelemetry.MeterName)</c>.
/// </summary>
public static class AuditingTelemetry
{
    public const string MeterName = "Boilerplate.Modules.Auditing";

    internal static readonly Meter Meter = new(MeterName);

    /// <summary>Successful publish to the channel (excluding drops).</summary>
    internal static readonly Counter<long> Published = Meter.CreateCounter<long>(
        "boilerplate.audit.published",
        unit: "{event}",
        description: "Number of audit events accepted by the channel publisher.");

    /// <summary>
    /// Channel was full and the bounded-channel policy evicted an older event
    /// to make room. Use this counter to alarm on sustained pressure — a
    /// non-zero rate over minutes is a signal that the sink can't keep up.
    /// </summary>
    internal static readonly Counter<long> Dropped = Meter.CreateCounter<long>(
        "boilerplate.audit.dropped",
        unit: "{event}",
        description: "Number of audit events dropped due to channel saturation.");

    /// <summary>Successful sink batch flush.</summary>
    internal static readonly Counter<long> Flushed = Meter.CreateCounter<long>(
        "boilerplate.audit.flushed",
        unit: "{event}",
        description: "Number of audit events successfully written by the sink.");

    /// <summary>
    /// Sink batch flush threw — counts events that will be retried (unless
    /// retry attempts are exhausted, in which case <see cref="DeadLettered"/>
    /// also increments).
    /// </summary>
    internal static readonly Counter<long> FlushFailed = Meter.CreateCounter<long>(
        "boilerplate.audit.flush_failed",
        unit: "{batch}",
        description: "Number of audit sink batches that failed to write.");

    /// <summary>
    /// Events that exhausted retries and were written to the dead-letter
    /// log. Should always alarm at threshold = 1.
    /// </summary>
    internal static readonly Counter<long> DeadLettered = Meter.CreateCounter<long>(
        "boilerplate.audit.dead_lettered",
        unit: "{event}",
        description: "Number of audit events written to the dead-letter sink after retries exhausted.");

    /// <summary>End-to-end sink flush latency including retries.</summary>
    internal static readonly Histogram<double> FlushDurationMs = Meter.CreateHistogram<double>(
        "boilerplate.audit.flush.duration",
        unit: "ms",
        description: "Sink flush duration including retries.");
}
