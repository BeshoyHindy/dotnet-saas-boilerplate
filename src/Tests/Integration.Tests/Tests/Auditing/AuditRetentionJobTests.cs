using Boilerplate.BuildingBlocks.Shared.Multitenancy;
using Boilerplate.Modules.Auditing;
using Boilerplate.Modules.Auditing.Contracts;
using Boilerplate.Modules.Auditing.Persistence;
using Integration.Tests.Infrastructure;

namespace Integration.Tests.Tests.Auditing;

/// <summary>
/// The retention sweep is a <c>[SystemJob]</c>: the scheduler triggers it with no tenant in scope,
/// yet <see cref="AuditDbContext"/> is tenant-filtered. Before the fan-out it queried that context
/// with a null ambient tenant, so the first sweep threw as soon as
/// <see cref="AuditRetentionOptions.Enabled"/> was turned on — invisible because the option is off
/// by default.
///
/// The assertion that matters is "every tenant, and only what is past its window": two tenants each
/// get one stale row and one fresh row, and one run must take exactly the two stale ones.
/// </summary>
[Collection(AppCollectionDefinition.Name)]
public sealed class AuditRetentionJobTests
{
    private readonly AppWebApplicationFactory _factory;
    private readonly TenantFixtures _tenants;

    public AuditRetentionJobTests(AppWebApplicationFactory factory)
    {
        _factory = factory;
        _tenants = new TenantFixtures(factory);
    }

    [Fact]
    public async Task Retention_Should_Purge_Stale_Rows_For_Every_Tenant_And_Keep_Recent_Ones()
    {
        var (tenantA, _) = await _tenants.CreateProvisionedTenantAsync("auditreta");
        var (tenantB, _) = await _tenants.CreateProvisionedTenantAsync("auditretb");

        var now = DateTime.UtcNow;
        var staleA = await SeedAuditAsync(tenantA, now.AddDays(-400));
        var freshA = await SeedAuditAsync(tenantA, now.AddMinutes(-5));
        var staleB = await SeedAuditAsync(tenantB, now.AddDays(-400));
        var freshB = await SeedAuditAsync(tenantB, now.AddMinutes(-5));

        await RunRetentionJobAsync();

        (await AuditExistsAsync(tenantA, staleA)).ShouldBeFalse("tenant A's row is past the activity window");
        (await AuditExistsAsync(tenantB, staleB)).ShouldBeFalse(
            "the sweep must reach the second tenant too, not just whichever one happened to be ambient");
        (await AuditExistsAsync(tenantA, freshA)).ShouldBeTrue("tenant A's fresh row is inside the window");
        (await AuditExistsAsync(tenantB, freshB)).ShouldBeTrue("tenant B's fresh row is inside the window");
    }

    [Fact]
    public async Task Retention_Should_Do_Nothing_When_Disabled()
    {
        var (tenantId, _) = await _tenants.CreateProvisionedTenantAsync("auditretoff");
        var stale = await SeedAuditAsync(tenantId, DateTime.UtcNow.AddDays(-400));

        await RunRetentionJobAsync(enabled: false);

        (await AuditExistsAsync(tenantId, stale)).ShouldBeTrue("the master switch is off, so nothing may be deleted");
    }

    // ─── helpers ─────────────────────────────────────────────────────

    /// <summary>
    /// Builds the job with retention switched on. The option object is a singleton read from
    /// configuration at start-up, so it is supplied here instead of reconfiguring the whole host.
    /// </summary>
    private async Task RunRetentionJobAsync(bool enabled = true)
    {
        var options = new AuditRetentionOptions
        {
            Enabled = enabled,
            ActivityRetentionDays = 30,
            DeleteBatchSize = 100,
        };

        using var scope = _factory.Services.CreateScope();
        // Mirrors Hangfire: the job is constructed by the activator, not resolved from the container.
        var job = ActivatorUtilities.CreateInstance<AuditRetentionJob>(scope.ServiceProvider, options);
        await job.RunAsync(CancellationToken.None);
    }

    private async Task<Guid> SeedAuditAsync(string tenantId, DateTime occurredAtUtc)
    {
        var id = Guid.CreateVersion7();

        await _factory.Services.GetRequiredService<ITenantScope>().RunAsync(tenantId, async (services, ct) =>
        {
            var db = services.GetRequiredService<AuditDbContext>();
            db.AuditRecords.Add(new AuditRecord
            {
                Id = id,
                OccurredAtUtc = occurredAtUtc,
                ReceivedAtUtc = occurredAtUtc,
                EventType = (int)AuditEventType.Activity,
                Severity = 0,
                Source = "retention-test",
                PayloadJson = "{}",
            });
            await db.SaveChangesAsync(ct);
        });

        return id;
    }

    private Task<bool> AuditExistsAsync(string tenantId, Guid id) =>
        _factory.Services.GetRequiredService<ITenantScope>().RunAsync(
            tenantId,
            (services, ct) => services.GetRequiredService<AuditDbContext>()
                .AuditRecords.AnyAsync(a => a.Id == id, ct));
}
