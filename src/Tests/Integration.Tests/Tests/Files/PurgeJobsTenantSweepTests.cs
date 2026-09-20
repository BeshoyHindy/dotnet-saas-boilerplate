using Boilerplate.BuildingBlocks.Persistence;
using Boilerplate.BuildingBlocks.Shared.Multitenancy;
using Boilerplate.Modules.Files.Contracts.v1.DTOs;
using Boilerplate.Modules.Files.Data;
using Boilerplate.Modules.Files.Domain;
using Boilerplate.Modules.Files.Jobs;
using Integration.Tests.Infrastructure;

namespace Integration.Tests.Tests.Files;

/// <summary>
/// The two purge sweeps are <c>[SystemJob]</c>s. They used to read the database with
/// <c>IgnoreQueryFilters()</c> standing in for a tenant, which made them cross-tenant by accident;
/// they are now tenant sweeps, fanning out through <c>ITenantScope.RunForEachTenantAsync</c> and
/// lifting only the named <c>SoftDelete</c> filter inside each pass (#74).
///
/// The proof is rows seeded into two different ordinary tenants plus a control row in root: one run
/// must take all of them, and must leave the rows that are still inside their window alone. A sweep
/// that visited only the ambient tenant would fail here.
/// </summary>
[Collection(AppCollectionDefinition.Name)]
public sealed class PurgeJobsTenantSweepTests
{
    private readonly AppWebApplicationFactory _factory;
    private readonly TenantFixtures _tenants;

    public PurgeJobsTenantSweepTests(AppWebApplicationFactory factory)
    {
        _factory = factory;
        _tenants = new TenantFixtures(factory);
    }

    [Fact]
    public async Task PurgeDeletedFiles_Should_Sweep_Every_Tenant_In_One_Run()
    {
        var (first, _) = await _tenants.CreateProvisionedTenantAsync("filedel1");
        var (second, _) = await _tenants.CreateProvisionedTenantAsync("filedel2");

        var purgeableInFirst = await SeedSoftDeletedFileAsync(first, DateTimeOffset.UtcNow.AddDays(-90));
        var recentInFirst = await SeedSoftDeletedFileAsync(first, DateTimeOffset.UtcNow);
        var purgeableInSecond = await SeedSoftDeletedFileAsync(second, DateTimeOffset.UtcNow.AddDays(-90));
        var purgeableInRoot = await SeedSoftDeletedFileAsync(
            TestConstants.RootTenantId, DateTimeOffset.UtcNow.AddDays(-90));

        await RunJobAsync<PurgeDeletedFilesJob>(j => j.RunAsync(CancellationToken.None));

        (await FileExistsAsync(first, purgeableInFirst)).ShouldBeFalse(
            "one run must visit every tenant, not just the one that happens to be ambient");
        (await FileExistsAsync(second, purgeableInSecond)).ShouldBeFalse(
            "the second tenant must be swept by the same run");
        (await FileExistsAsync(TestConstants.RootTenantId, purgeableInRoot)).ShouldBeFalse(
            "root is a tenant like any other and must be swept too");
        (await FileExistsAsync(first, recentInFirst)).ShouldBeTrue(
            "this one is still inside the retention window");
    }

    [Fact]
    public async Task PurgeOrphanedFiles_Should_Sweep_Every_Tenant_In_One_Run()
    {
        var (first, _) = await _tenants.CreateProvisionedTenantAsync("fileorp1");
        var (second, _) = await _tenants.CreateProvisionedTenantAsync("fileorp2");

        var expiredInFirst = await SeedPendingFileAsync(first, DateTimeOffset.UtcNow.AddHours(-2));
        var liveInFirst = await SeedPendingFileAsync(first, DateTimeOffset.UtcNow.AddHours(2));
        var expiredInSecond = await SeedPendingFileAsync(second, DateTimeOffset.UtcNow.AddHours(-2));

        await RunJobAsync<PurgeOrphanedFilesJob>(j => j.RunAsync(CancellationToken.None));

        (await FileExistsAsync(first, expiredInFirst)).ShouldBeFalse(
            "an expired pending row must be purged in every tenant the sweep visits");
        (await FileExistsAsync(second, expiredInSecond)).ShouldBeFalse(
            "the second tenant must be swept by the same run");
        (await FileExistsAsync(first, liveInFirst)).ShouldBeTrue("its upload window has not closed yet");
    }

    // ─── helpers ─────────────────────────────────────────────────────

    private async Task RunJobAsync<TJob>(Func<TJob, Task> invoke) where TJob : notnull
    {
        using var scope = _factory.Services.CreateScope();
        // The purge jobs are scheduled through IRecurringJobManager and constructed by Hangfire's
        // activator rather than resolved from the container, so we mirror that here.
        var job = ActivatorUtilities.CreateInstance<TJob>(scope.ServiceProvider);
        await invoke(job);
    }

    private async Task<Guid> SeedSoftDeletedFileAsync(string tenantId, DateTimeOffset deletedOnUtc)
    {
        var id = await SeedPendingFileAsync(tenantId, DateTimeOffset.UtcNow.AddHours(2));

        await InTenantAsync(tenantId, (db, ct) => db.FileAssets
            .Where(f => f.Id == id)
            .ExecuteUpdateAsync(
                s => s.SetProperty(f => f.IsDeleted, true)
                      .SetProperty(f => f.DeletedOnUtc, deletedOnUtc)
                      .SetProperty(f => f.Status, FileAssetStatus.Available),
                ct));

        return id;
    }

    private async Task<Guid> SeedPendingFileAsync(string tenantId, DateTimeOffset uploadDeadline)
    {
        var id = Guid.CreateVersion7();

        await InTenantAsync(tenantId, async (db, ct) =>
        {
            db.FileAssets.Add(FileAsset.CreatePending(
                id,
                ownerType: "MyFiles",
                ownerId: null,
                originalFileName: "purge-fanout.pdf",
                sanitizedFileName: "purge-fanout.pdf",
                contentType: "application/pdf",
                declaredSizeBytes: 128,
                storageKey: $"tests/purge-fanout/{id:N}",
                visibility: Visibility.Private,
                createdByUserId: "purge-fanout-seed",
                uploadDeadline: uploadDeadline));
            await db.SaveChangesAsync(ct);
        });

        return id;
    }

    /// <summary>
    /// Soft-deleted rows are hidden by the named <c>SoftDelete</c> filter; the tenant filter stays on,
    /// so this reads that tenant's rows and nothing else.
    /// </summary>
    private Task<bool> FileExistsAsync(string tenantId, Guid id) =>
        _factory.Services.GetRequiredService<ITenantScope>().RunAsync(
            tenantId,
            (services, ct) => services.GetRequiredService<FilesDbContext>()
                .FileAssets
                .IgnoreQueryFilters([QueryFilters.SoftDelete])
                .AnyAsync(f => f.Id == id, ct));

    private Task InTenantAsync(string tenantId, Func<FilesDbContext, CancellationToken, Task> work) =>
        _factory.Services.GetRequiredService<ITenantScope>().RunAsync(
            tenantId,
            (services, ct) => work(services.GetRequiredService<FilesDbContext>(), ct));
}
