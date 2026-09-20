using Boilerplate.BuildingBlocks.Jobs;
using Boilerplate.BuildingBlocks.Shared.Multitenancy;
using Boilerplate.BuildingBlocks.Storage.Services;
using Boilerplate.Modules.Files.Contracts.v1.DTOs;
using Boilerplate.Modules.Files.Data;
using Boilerplate.Modules.Files.Domain;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Boilerplate.Modules.Files.Jobs;

/// <summary>
/// Hourly purge of FileAsset rows stuck in PendingUpload past their UploadDeadline. Best-effort
/// removal of any bytes that did make it to storage.
/// </summary>
/// <remarks>
/// <see cref="SystemJobAttribute"/>: the sweep itself belongs to no tenant — it is the fan-out. Each
/// tenant is entered explicitly through <see cref="ITenantScope"/>, so a tenant with a dedicated
/// connection string is swept in its own database rather than missed entirely. No query filter is
/// lifted: a pending row was never deleted, and the tenant filter is what confines each pass.
/// </remarks>
[SystemJob]
public sealed class PurgeOrphanedFilesJob(
    ITenantScope tenantScope,
    ILogger<PurgeOrphanedFilesJob> logger)
{
    [AutomaticRetry(Attempts = 3, DelaysInSeconds = [30, 120, 600])]
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;

        await tenantScope.RunForEachTenantAsync(
            async (tenant, services, ct) =>
            {
                try
                {
                    await PurgeTenantAsync(services, now, ct).ConfigureAwait(false);
                }
#pragma warning disable CA1031 // One tenant's failure must not stop the rest of the sweep
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    logger.LogError(ex, "Purging orphaned files failed for tenant {TenantId}", tenant.Id);
                }
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task PurgeTenantAsync(
        IServiceProvider tenantServices, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // Both the context and the storage client come from the tenant's own scope: FilesDbContext
        // captures its TenantInfo — and with it the tenant filter — at construction.
        var db = tenantServices.GetRequiredService<FilesDbContext>();
        var storage = tenantServices.GetRequiredService<IStorageService>();

        var orphans = await db.FileAssets
            .Where(f => f.Status == FileAssetStatus.PendingUpload
                        && f.UploadDeadline != null
                        && f.UploadDeadline < now)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (orphans.Count == 0)
        {
            return;
        }

        foreach (var f in orphans)
        {
            try
            {
                await storage.RemoveAsync(f.StorageKey, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to remove orphan storage object {Key}", f.StorageKey);
            }
            // Hard delete (row never reached Available, so soft-delete doesn't apply). FileAsset is ISoftDeletable,
            // so Remove() would become UPDATE IsDeleted=true — we use the bulk ExecuteDelete below to bypass the interceptor instead.
        }

        // Bulk hard delete — bypasses the soft-delete interceptor.
        var ids = orphans.Select(f => f.Id).ToList();
        await db.FileAssets
            .Where(f => ids.Contains(f.Id))
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("Purged {Count} orphaned file assets", orphans.Count);
        }
    }
}
