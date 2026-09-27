using Boilerplate.BuildingBlocks.Eventing.Persistence;
using Boilerplate.BuildingBlocks.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Boilerplate.BuildingBlocks.Eventing.Retention;

/// <summary>
/// Daily purge of the outbox and inbox. The dispatcher only stamps an outbox row processed and the
/// inbox writes one row per (event, handler), so without this both tables grow for as long as the
/// installation runs. Deletes processed outbox rows and inbox rows older than
/// <see cref="EventingOptions.ProcessedRetentionDays"/>, in batches of
/// <see cref="EventingOptions.RetentionDeleteBatchSize"/> so no statement holds a long lock.
/// </summary>
/// <remarks>
/// <see cref="SystemJobAttribute"/>: the outbox and inbox are global tables (<c>IGlobalEntity</c>)
/// that the dispatcher already reads tenant-less, so the purge is one pass over them, not a
/// tenant sweep. Unprocessed rows and dead-lettered rows are left alone: the first are still owed a
/// dispatch, the second are what a redrive recovers.
/// </remarks>
[SystemJob]
public sealed partial class EventingRetentionJob
{
    private readonly EventingDbContext _db;
    private readonly EventingOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<EventingRetentionJob> _logger;

    public EventingRetentionJob(
        EventingDbContext db,
        IOptions<EventingOptions> options,
        TimeProvider timeProvider,
        ILogger<EventingRetentionJob> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        _db = db;
        _options = options.Value;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        if (_options.ProcessedRetentionDays <= 0)
        {
            LogSkipped();
            return;
        }

        var cutoffUtc = _timeProvider.GetUtcNow().UtcDateTime.AddDays(-_options.ProcessedRetentionDays);
        var batchSize = Math.Max(1, _options.RetentionDeleteBatchSize);

        var outbox = await PurgeOutboxAsync(cutoffUtc, batchSize, ct).ConfigureAwait(false);
        var inbox = await PurgeInboxAsync(cutoffUtc, batchSize, ct).ConfigureAwait(false);

        LogPurged(outbox, inbox, cutoffUtc);
    }

    private async Task<long> PurgeOutboxAsync(DateTime cutoffUtc, int batchSize, CancellationToken ct)
    {
        long purged = 0;
        while (!ct.IsCancellationRequested)
        {
            // ExecuteDeleteAsync has no LIMIT, so the batch is bounded by an id sub-query.
            // !IsDead first lets IX_OutboxMessages_Pending (IsDead, ProcessedOnUtc, …) serve the scan.
            var deleted = await _db.OutboxMessages
                .Where(o => _db.OutboxMessages
                    .Where(b => !b.IsDead && b.ProcessedOnUtc != null && b.ProcessedOnUtc < cutoffUtc)
                    .OrderBy(b => b.ProcessedOnUtc)
                    .Select(b => b.Id)
                    .Take(batchSize)
                    .Contains(o.Id))
                .ExecuteDeleteAsync(ct)
                .ConfigureAwait(false);

            purged += deleted;
            if (deleted < batchSize) break;
        }

        return purged;
    }

    private async Task<long> PurgeInboxAsync(DateTime cutoffUtc, int batchSize, CancellationToken ct)
    {
        long purged = 0;
        while (!ct.IsCancellationRequested)
        {
            // The inbox key is (Id, HandlerName), so the bounded batch is matched on both columns.
            var deleted = await _db.InboxMessages
                .Where(i => _db.InboxMessages
                    .Where(b => b.ProcessedOnUtc < cutoffUtc)
                    .OrderBy(b => b.ProcessedOnUtc)
                    .Take(batchSize)
                    .Any(b => b.Id == i.Id && b.HandlerName == i.HandlerName))
                .ExecuteDeleteAsync(ct)
                .ConfigureAwait(false);

            purged += deleted;
            if (deleted < batchSize) break;
        }

        return purged;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "[Eventing] retention purge skipped (ProcessedRetentionDays <= 0).")]
    private partial void LogSkipped();

    [LoggerMessage(Level = LogLevel.Information, Message = "[Eventing] retention purge deleted {Outbox} processed outbox rows and {Inbox} inbox rows older than {CutoffUtc:o}.")]
    private partial void LogPurged(long outbox, long inbox, DateTime cutoffUtc);
}
