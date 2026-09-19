using Boilerplate.BuildingBlocks.Jobs;
using Boilerplate.BuildingBlocks.Shared.Multitenancy;
using Boilerplate.Modules.Auditing.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Boilerplate.Modules.Auditing.Persistence;

/// <summary>
/// Daily Hangfire job that prunes the audit table per
/// <see cref="AuditRetentionOptions"/>. Uses <c>ExecuteDeleteAsync</c> with
/// a bounded batch size so a single run doesn't take a long-held lock on
/// the table — each event-type sweep loops until fewer than batch-size
/// rows are deleted.
/// </summary>
/// <remarks>
/// <see cref="SystemJobAttribute"/>: the sweep itself belongs to no tenant — it is the fan-out. Each
/// tenant is entered explicitly through <see cref="ITenantScope"/>, so the
/// <see cref="AuditDbContext"/> is built with that tenant ambient (its filter would otherwise
/// dereference a null tenant) and against that tenant's own connection string.
/// </remarks>
[SystemJob]
public sealed class AuditRetentionJob
{
    private readonly ITenantScope _tenantScope;
    private readonly AuditRetentionOptions _opts;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AuditRetentionJob> _logger;

    public AuditRetentionJob(
        ITenantScope tenantScope,
        AuditRetentionOptions opts,
        TimeProvider timeProvider,
        ILogger<AuditRetentionJob> logger)
    {
        _tenantScope = tenantScope;
        _opts = opts;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        if (!_opts.Enabled)
        {
            _logger.LogInformation("[Auditing] retention job skipped (Enabled=false).");
            return;
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        long total = 0;

        await _tenantScope.RunForEachTenantAsync(
            async (tenant, services, tenantCt) =>
            {
                try
                {
                    total += await SweepTenantAsync(
                        services.GetRequiredService<AuditDbContext>(), now, tenantCt).ConfigureAwait(false);
                }
#pragma warning disable CA1031 // One tenant's failure must not stop the rest of the sweep
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    _logger.LogError(ex, "[Auditing] retention sweep failed for tenant {TenantId}", tenant.Id);
                }
            },
            ct).ConfigureAwait(false);

        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("[Auditing] retention job purged {Total} rows.", total);
        }
    }

    private async Task<long> SweepTenantAsync(AuditDbContext db, DateTime now, CancellationToken ct)
    {
        long total = 0;
        total += await SweepAsync(db, AuditEventType.Activity, now.AddDays(-_opts.ActivityRetentionDays), ct).ConfigureAwait(false);
        total += await SweepAsync(db, AuditEventType.EntityChange, now.AddDays(-_opts.EntityChangeRetentionDays), ct).ConfigureAwait(false);
        total += await SweepAsync(db, AuditEventType.Security, now.AddDays(-_opts.SecurityRetentionDays), ct).ConfigureAwait(false);
        total += await SweepAsync(db, AuditEventType.Exception, now.AddDays(-_opts.ExceptionRetentionDays), ct).ConfigureAwait(false);
        return total;
    }

    private async Task<long> SweepAsync(AuditDbContext db, AuditEventType eventType, DateTime cutoffUtc, CancellationToken ct)
    {
        long swept = 0;
        var typeId = (int)eventType;
        var batchSize = Math.Max(100, _opts.DeleteBatchSize);

        while (!ct.IsCancellationRequested)
        {
            // Sub-query trick: ExecuteDeleteAsync doesn't support TOP/LIMIT
            // directly, so we filter to a bounded id-set first. The tenant filter is
            // in force on both queries — this deletes only the ambient tenant's rows.
            var deleted = await db.AuditRecords
                .Where(a => a.EventType == typeId
                    && a.OccurredAtUtc < cutoffUtc
                    && db.AuditRecords
                        .Where(b => b.EventType == typeId && b.OccurredAtUtc < cutoffUtc)
                        .OrderBy(b => b.OccurredAtUtc)
                        .Select(b => b.Id)
                        .Take(batchSize)
                        .Contains(a.Id))
                .ExecuteDeleteAsync(ct)
                .ConfigureAwait(false);

            swept += deleted;
            if (deleted < batchSize) break;
        }

        if (swept > 0 && _logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("[Auditing] purged {Count} {EventType} events older than {Cutoff:o}.",
                swept, eventType, cutoffUtc);
        }
        return swept;
    }
}
