namespace Boilerplate.BuildingBlocks.Eventing;

/// <summary>
/// Configuration options for the eventing building block.
/// </summary>
public sealed class EventingOptions
{
    /// <summary>
    /// Batch size for outbox dispatching.
    /// </summary>
    public int OutboxBatchSize { get; set; } = 100;

    /// <summary>
    /// Maximum number of retries before an outbox message is marked as dead.
    /// </summary>
    public int OutboxMaxRetries { get; set; } = 5;

    /// <summary>
    /// Base delay (seconds) for exponential retry backoff after a failed dispatch. The n-th retry
    /// waits <c>base * 2^(n-1)</c>, capped at <see cref="OutboxRetryMaxDelaySeconds"/>.
    /// </summary>
    public int OutboxRetryBaseDelaySeconds { get; set; } = 30;

    /// <summary>
    /// Upper bound (seconds) on the exponential retry backoff.
    /// </summary>
    public int OutboxRetryMaxDelaySeconds { get; set; } = 3600;

    /// <summary>
    /// Seconds a claimed outbox row stays leased to one dispatcher. Must exceed the worst-case
    /// time to publish a batch, or a second instance re-claims rows still in flight and
    /// double-publishes them.
    /// </summary>
    public int OutboxClaimLeaseSeconds { get; set; } = 300;

    /// <summary>
    /// Whether inbox-based idempotent handling is enabled.
    /// </summary>
    public bool EnableInbox { get; set; } = true;

    /// <summary>
    /// Interval in seconds for the outbox dispatcher background service.
    /// Set to 0 to disable the background service (use Hangfire instead).
    /// </summary>
    public int OutboxDispatchIntervalSeconds { get; set; } = 10;

    /// <summary>
    /// Whether to use the hosted service for outbox dispatching.
    /// If false, you should configure Hangfire or another scheduler.
    /// </summary>
    public bool UseHostedServiceDispatcher { get; set; } = true;

    /// <summary>
    /// Days a processed outbox row, and an inbox row, are kept before
    /// <see cref="Retention.EventingRetentionJob"/> deletes them. Unprocessed and dead-lettered
    /// outbox rows are never deleted. An inbox row is what makes a handler idempotent, so an event
    /// redelivered after this window would be handled again; keep it longer than any redelivery you
    /// expect. Zero or less switches the purge off.
    /// </summary>
    public int ProcessedRetentionDays { get; set; } = 7;

    /// <summary>
    /// Maximum rows deleted per statement by the retention purge. The purge loops until a
    /// statement deletes fewer, so each one holds its locks only briefly.
    /// </summary>
    public int RetentionDeleteBatchSize { get; set; } = 1_000;

    /// <summary>
    /// Cron expression (UTC) for the retention purge. Daily at 03:45 by default.
    /// </summary>
    public string RetentionCron { get; set; } = "45 3 * * *";
}
