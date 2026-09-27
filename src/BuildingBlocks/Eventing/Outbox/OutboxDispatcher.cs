using Boilerplate.BuildingBlocks.Eventing.Abstractions;
using Boilerplate.BuildingBlocks.Eventing.Telemetry;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Boilerplate.BuildingBlocks.Eventing.Outbox;

/// <summary>
/// Dispatches outbox messages via the configured event bus.
/// This type is intended to be invoked by a scheduler (e.g., Hangfire recurring job or hosted service).
/// </summary>
public sealed partial class OutboxDispatcher
{
    /// <summary>
    /// Identifies this process in the lease it takes on a row. Diagnostic only — correctness comes
    /// from SKIP LOCKED, not from the identifier being unique.
    /// </summary>
    private static readonly string InstanceId =
        $"{Environment.MachineName}:{Environment.ProcessId}";

    private readonly IOutboxStore _outbox;
    private readonly IEventBus _bus;
    private readonly IEventSerializer _serializer;
    private readonly ILogger<OutboxDispatcher> _logger;
    private readonly EventingOptions _options;

    public OutboxDispatcher(
        IOutboxStore outbox,
        IEventBus bus,
        IEventSerializer serializer,
        IOptions<EventingOptions> options,
        ILogger<OutboxDispatcher> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        _outbox = outbox;
        _bus = bus;
        _serializer = serializer;
        _logger = logger;
        _options = options.Value;
    }

    public async Task DispatchAsync(CancellationToken ct = default)
    {
        var batchSize = _options.OutboxBatchSize;
        if (batchSize <= 0) batchSize = 100;

        var lease = TimeSpan.FromSeconds(_options.OutboxClaimLeaseSeconds > 0 ? _options.OutboxClaimLeaseSeconds : 300);
        var messages = await _outbox.ClaimBatchAsync(batchSize, InstanceId, lease, ct).ConfigureAwait(false);
        if (messages.Count == 0)
        {
            // Rows backing off before a retry are pending but unclaimable, so sample the backlog
            // even on a pass that found nothing to do.
            await RecordPendingAsync(ct).ConfigureAwait(false);
            _logger.LogDebug("No outbox messages to dispatch.");
            return;
        }

        LogDispatching(messages.Count, batchSize);

        var processedCount = 0;
        var failedCount = 0;
        var deadLetterCount = 0;

        foreach (var message in messages)
        {
            using var activity = StartDispatchActivity(message);
            try
            {
                var @event = _serializer.Deserialize(message.Payload, message.Type);
                if (@event is null)
                {
                    await _outbox.MarkAsFailedAsync(message, "Cannot deserialize integration event.", isDead: true, ct).ConfigureAwait(false);
                    activity?.SetStatus(ActivityStatusCode.Error, "Cannot deserialize integration event.");
                    continue;
                }

                await _bus.PublishAsync(@event, ct).ConfigureAwait(false);
                await _outbox.MarkAsProcessedAsync(message, ct).ConfigureAwait(false);
                processedCount++;
                EventingTelemetry.OutboxDispatched.Add(1);
                activity?.SetStatus(ActivityStatusCode.Ok);

                LogMessageDispatched(message.Id);
            }
            // Broad catch is intentional: each message must be processed independently,
            // and any failure type should trigger the retry/dead-letter mechanism.
            catch (Exception ex)
            {
                var maxRetries = _options.OutboxMaxRetries <= 0 ? 5 : _options.OutboxMaxRetries;
                var isDead = message.RetryCount + 1 >= maxRetries;

                await _outbox.MarkAsFailedAsync(message, ex.Message, isDead, ct).ConfigureAwait(false);

                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.SetTag("exception.type", ex.GetType().FullName);
                activity?.SetTag("boilerplate.outbox.dead_lettered", isDead);

                failedCount++;
                if (isDead)
                {
                    deadLetterCount++;
                    EventingTelemetry.OutboxDeadLettered.Add(1);
                }

                if (isDead)
                {
                    _logger.LogError(ex, "Outbox message {MessageId} moved to dead-letter after {RetryCount} retries", message.Id, message.RetryCount + 1);
                }
                else
                {
                    _logger.LogWarning(ex, "Outbox message {MessageId} failed (RetryCount={RetryCount}).", message.Id, message.RetryCount + 1);
                }
            }
        }

        LogDispatchSummary(messages.Count, processedCount, failedCount, deadLetterCount);
        await RecordPendingAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// One span per dispatch attempt, tagged with what an operator searches by: the event type,
    /// its tenant and the correlation id the publishing request stamped on it.
    /// </summary>
    private static Activity? StartDispatchActivity(OutboxMessage message)
    {
        var activity = EventingTelemetry.ActivitySource.StartActivity("Outbox.Dispatch", ActivityKind.Internal);
        if (activity is null)
        {
            return null;
        }

        activity.SetTag("messaging.message.id", message.Id);
        activity.SetTag("boilerplate.event_type", EventTypeName(message.Type));
        activity.SetTag("boilerplate.tenant_id", message.TenantId);
        activity.SetTag("boilerplate.outbox.retry_count", message.RetryCount);
        activity.SetTag(EventingTelemetry.CorrelationIdTag, message.CorrelationId);
        return activity;
    }

    /// <summary>The type's full name without the assembly qualification the outbox stores.</summary>
    private static string EventTypeName(string assemblyQualifiedName)
    {
        var comma = assemblyQualifiedName.IndexOf(',', StringComparison.Ordinal);
        return comma < 0 ? assemblyQualifiedName : assemblyQualifiedName[..comma];
    }

    /// <summary>
    /// Samples the backlog for the pending gauge. Telemetry only: a failure here is logged and
    /// never fails the dispatch pass.
    /// </summary>
    private async Task RecordPendingAsync(CancellationToken ct)
    {
        try
        {
            var pending = await _outbox.CountPendingAsync(ct).ConfigureAwait(false);
            EventingTelemetry.RecordOutboxPending(pending);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogPendingCountFailed(ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not count pending outbox messages for the pending gauge.")]
    private partial void LogPendingCountFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Dispatching {Count} outbox messages (BatchSize={BatchSize})")]
    private partial void LogDispatching(int count, int batchSize);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Outbox message {MessageId} dispatched and marked as processed.")]
    private partial void LogMessageDispatched(Guid messageId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Outbox dispatch summary: Total={Total}, Processed={Processed}, Failed={Failed}, DeadLettered={DeadLettered}")]
    private partial void LogDispatchSummary(int total, int processed, int failed, int deadLettered);
}