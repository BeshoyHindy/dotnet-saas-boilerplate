using Boilerplate.BuildingBlocks.Persistence;
using Boilerplate.BuildingBlocks.Shared.Multitenancy;
using Boilerplate.Modules.Files.Contracts.v1.DTOs;
using Boilerplate.Modules.Files.Data;
using Boilerplate.Modules.Files.Domain;
using Boilerplate.Modules.Files.Jobs;
using Integration.Tests.Infrastructure;

namespace Integration.Tests.Tests.Files;

/// <summary>
/// The two purge sweeps are <c>[SystemJob]</c>s. They used to read whichever database the default
/// connection pointed at, with <c>IgnoreQueryFilters()</c> standing in for a tenant — which means a
/// tenant with a <b>dedicated connection string</b> was never swept: its rows live in another
/// database entirely, and the sweep never opened it.
///
/// The proof is a row seeded into a tenant's own database (the second database lives in the same
/// Postgres container, created by provisioning's migrate step) plus a control row in the default
/// one: a single run must take both.
/// </summary>
[Collection(AppCollectionDefinition.Name)]
public sealed class PurgeJobsDedicatedDatabaseTests
{
    private readonly AppWebApplicationFactory _factory;
    private readonly TenantFixtures _tenants;

    public PurgeJobsDedicatedDatabaseTests(AppWebApplicationFactory factory)
    {
        _factory = factory;
        _tenants = new TenantFixtures(factory);
    }

    [Fact]
    public async Task PurgeDeletedFiles_Should_Reach_A_Tenant_With_Its_Own_Database()
    {
        var tenantId = await CreateTenantWithOwnDatabaseAsync("filedelded");

        var purgeable = await SeedSoftDeletedFileAsync(tenantId, DateTimeOffset.UtcNow.AddDays(-90));
        var recent = await SeedSoftDeletedFileAsync(tenantId, DateTimeOffset.UtcNow);
        var inDefaultDatabase = await SeedSoftDeletedFileAsync(
            TestConstants.RootTenantId, DateTimeOffset.UtcNow.AddDays(-90));

        await RunJobAsync<PurgeDeletedFilesJob>(j => j.RunAsync(CancellationToken.None));

        (await FileExistsAsync(tenantId, purgeable)).ShouldBeFalse(
            "the sweep must open the tenant's own database, not only the default one");
        (await FileExistsAsync(tenantId, recent)).ShouldBeTrue("this one is still inside the retention window");
        (await FileExistsAsync(TestConstants.RootTenantId, inDefaultDatabase)).ShouldBeFalse(
            "the default database must still be swept");
    }

    [Fact]
    public async Task PurgeOrphanedFiles_Should_Reach_A_Tenant_With_Its_Own_Database()
    {
        var tenantId = await CreateTenantWithOwnDatabaseAsync("fileorpded");

        var expired = await SeedPendingFileAsync(tenantId, DateTimeOffset.UtcNow.AddHours(-2));
        var live = await SeedPendingFileAsync(tenantId, DateTimeOffset.UtcNow.AddHours(2));

        await RunJobAsync<PurgeOrphanedFilesJob>(j => j.RunAsync(CancellationToken.None));

        (await FileExistsAsync(tenantId, expired)).ShouldBeFalse(
            "the expired pending row lives in the tenant's own database and must be purged there");
        (await FileExistsAsync(tenantId, live)).ShouldBeTrue("its upload window has not closed yet");
    }

    // ─── helpers ─────────────────────────────────────────────────────

    private async Task<string> CreateTenantWithOwnDatabaseAsync(string prefix)
    {
        // EF's Migrate() creates a Postgres database that does not exist yet, so provisioning's
        // migrate step both creates and schemas this one — exactly the production path.
        var databaseName = $"tenant_{prefix}_{Guid.NewGuid():N}"[..40];
        var (tenantId, _) = await _tenants.CreateProvisionedTenantAsync(
            prefix, _factory.ConnectionStringForDatabase(databaseName));
        return tenantId;
    }

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
    /// so this reads the tenant's own database and nothing else.
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
