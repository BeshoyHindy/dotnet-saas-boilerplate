using Boilerplate.BuildingBlocks.Storage.Services;
using Boilerplate.Modules.Files.Data;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Boilerplate.Modules.Files.Jobs;

/// <summary>
/// Daily purge of soft-deleted FileAsset rows past the retention window. Hard-deletes the row and
/// removes the bytes from storage.
/// </summary>
public sealed class PurgeDeletedFilesJob(
    FilesDbContext db,
    IStorageService storage,
    IOptions<FilesOptions> options,
    ILogger<PurgeDeletedFilesJob> logger)
{
    [AutomaticRetry(Attempts = 2, DelaysInSeconds = [300, 1800])]
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-options.Value.SoftDeleteRetentionDays);
        var candidates = await db.FileAssets
            .IgnoreQueryFilters()
            .Where(f => f.IsDeleted && f.DeletedOnUtc != null && f.DeletedOnUtc < cutoff)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (candidates.Count == 0)
        {
            return;
        }

        // Best-effort byte removal per file. Schema-per-tenant means all rows share one tenant,
        // and Hangfire wires the job per-tenant for multi-tenant deployments.
        foreach (var f in candidates)
        {
            try
            {
                await storage.RemoveAsync(f.StorageKey, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Storage remove failed for {Key}", f.StorageKey);
            }
        }

        var totalBytes = candidates.Sum(f => f.SizeBytes);

        var ids = candidates.Select(f => f.Id).ToList();
        await db.FileAssets
            .IgnoreQueryFilters()
            .Where(f => ids.Contains(f.Id))
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("Hard-purged {Count} soft-deleted file assets ({Bytes} bytes total)",
                candidates.Count, totalBytes);
        }
    }
}
