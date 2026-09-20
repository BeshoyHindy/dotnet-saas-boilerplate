using Boilerplate.BuildingBlocks.Jobs;
using Boilerplate.BuildingBlocks.Persistence;
using Boilerplate.BuildingBlocks.Shared.Multitenancy;
using Boilerplate.BuildingBlocks.Storage.Services;
using Boilerplate.Modules.Files.Data;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Boilerplate.Modules.Files.Jobs;

/// <summary>
/// Daily purge of soft-deleted FileAsset rows past the retention window. Hard-deletes the row and
/// removes the bytes from storage.
/// </summary>
/// <remarks>
/// <see cref="SystemJobAttribute"/>: the sweep itself belongs to no tenant — it is the fan-out. Each
/// tenant is entered explicitly through <see cref="ITenantScope"/>, so a tenant with a dedicated
/// connection string is swept in its own database rather than missed entirely. Only the named
/// <see cref="QueryFilters.SoftDelete"/> filter is lifted (these rows are deleted by definition);
/// the tenant filter stays in force, which is what confines each pass to the tenant it opened.
/// </remarks>
[SystemJob]
public sealed class PurgeDeletedFilesJob(
    ITenantScope tenantScope,
    IOptions<FilesOptions> options,
    ILogger<PurgeDeletedFilesJob> logger)
{
    [AutomaticRetry(Attempts = 2, DelaysInSeconds = [300, 1800])]
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-options.Value.SoftDeleteRetentionDays);

        await tenantScope.RunForEachTenantAsync(
            async (tenant, services, ct) =>
            {
                try
                {
                    await PurgeTenantAsync(services, cutoff, ct).ConfigureAwait(false);
                }
#pragma warning disable CA1031 // One tenant's failure must not stop the rest of the sweep
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    logger.LogError(ex, "Purging soft-deleted files failed for tenant {TenantId}", tenant.Id);
                }
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task PurgeTenantAsync(
        IServiceProvider tenantServices, DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        // Both the context and the storage client come from the tenant's own scope: FilesDbContext
        // captures its TenantInfo — and with it the tenant filter — at construction.
        var db = tenantServices.GetRequiredService<FilesDbContext>();
        var storage = tenantServices.GetRequiredService<IStorageService>();

        var candidates = await db.FileAssets
            .IgnoreQueryFilters([QueryFilters.SoftDelete])
            .Where(f => f.IsDeleted && f.DeletedOnUtc != null && f.DeletedOnUtc < cutoff)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (candidates.Count == 0)
        {
            return;
        }

        // Best-effort byte removal per file; a storage failure must not block the row purge.
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
            .IgnoreQueryFilters([QueryFilters.SoftDelete])
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
