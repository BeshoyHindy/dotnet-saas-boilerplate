using Boilerplate.BuildingBlocks.Eventing.Abstractions;
using Boilerplate.BuildingBlocks.Eventing.Persistence;
using Boilerplate.BuildingBlocks.Jobs.Services;
using Boilerplate.BuildingBlocks.Shared.Multitenancy;
using Integration.Tests.Infrastructure;
using Integration.Tests.Tests.Jobs;

namespace Integration.Tests.Tests.Multitenancy;

/// <summary>
/// ADR-0002, "Jobs and events", for the case that made the old code silently wrong: a tenant with a
/// <b>dedicated connection string</b>.
///
/// Both the job path and the event path used to install a tenant that carried identity only — the
/// row-level filter was satisfied, so nothing looked broken, while every read and write went to the
/// default database. The proof here is a row that exists in exactly one database: the probe writes
/// an inbox row keyed by a marker, and the test looks for it in the tenant's own database (present)
/// and in the default one (absent).
///
/// The second database lives in the same Postgres container — provisioning's migrate step creates
/// it the way it does in production — so this costs no extra container.
/// </summary>
[Collection(AppCollectionDefinition.Name)]
public sealed class DedicatedDatabaseTenantTests
{
    private static readonly TimeSpan JobTimeout = TimeSpan.FromSeconds(60);

    private readonly AppWebApplicationFactory _factory;
    private readonly TenantFixtures _tenants;

    public DedicatedDatabaseTenantTests(AppWebApplicationFactory factory)
    {
        _factory = factory;
        _tenants = new TenantFixtures(factory);
    }

    [Fact]
    public async Task A_Job_For_A_Tenant_With_Its_Own_Database_Should_Read_And_Write_That_Database()
    {
        var (tenantId, connectionString) = await CreateTenantWithOwnDatabaseAsync("jobded");
        var marker = Guid.CreateVersion7();

        var tenantScope = _factory.Services.GetRequiredService<ITenantScope>();
        await tenantScope.RunAsync(tenantId, (services, _) =>
        {
            services.GetRequiredService<IJobService>()
                .Enqueue<TenantProbeJob>(job => job.RunAsync(marker, CancellationToken.None));
            return Task.CompletedTask;
        });

        var observation = await WaitForAsync(() => TenantProbeJob.Observations.GetValueOrDefault(marker));

        observation.TenantId.ShouldBe(tenantId);
        observation.ConnectionString.ShouldBe(
            connectionString,
            "the job must run with the tenant's own connection string, not an id-only stub");

        await AssertMarkerIsOnlyInTheTenantDatabaseAsync(tenantId, marker);
    }

    [Fact]
    public async Task An_Event_Handler_For_A_Tenant_With_Its_Own_Database_Should_Read_And_Write_That_Database()
    {
        var (tenantId, connectionString) = await CreateTenantWithOwnDatabaseAsync("evtded");
        var marker = Guid.CreateVersion7();

        using (var scope = _factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IEventBus>().PublishAsync(
                new TenantProbeIntegrationEvent(
                    Guid.CreateVersion7(), DateTime.UtcNow, tenantId, $"corr-{marker:N}", "tests", marker));
        }

        TenantProbeHandler.Observations.ShouldContainKey(marker);
        var observation = TenantProbeHandler.Observations[marker];

        observation.TenantId.ShouldBe(tenantId);
        observation.ConnectionString.ShouldBe(
            connectionString,
            "the handler must run with the tenant's own connection string, not an id-only stub");

        await AssertMarkerIsOnlyInTheTenantDatabaseAsync(tenantId, marker);
    }

    // ─── helpers ─────────────────────────────────────────────────────

    private async Task<(string TenantId, string ConnectionString)> CreateTenantWithOwnDatabaseAsync(string prefix)
    {
        // EF's Migrate() creates a Postgres database that does not exist yet, so provisioning's
        // migrate step both creates and schemas this one — exactly the production path.
        var databaseName = $"tenant_{prefix}_{Guid.NewGuid():N}"[..40];
        var connectionString = _factory.ConnectionStringForDatabase(databaseName);

        var (tenantId, _) = await _tenants.CreateProvisionedTenantAsync(prefix, connectionString);
        return (tenantId, connectionString);
    }

    /// <summary>
    /// The marker row proves routing: present in the tenant's own database, absent from the default
    /// one. Asserting only the first would pass even if every tenant shared a database.
    /// </summary>
    private async Task AssertMarkerIsOnlyInTheTenantDatabaseAsync(string tenantId, Guid marker)
    {
        var tenantScope = _factory.Services.GetRequiredService<ITenantScope>();

        var inTenantDatabase = await tenantScope.RunAsync(
            tenantId,
            (services, ct) => services.GetRequiredService<EventingDbContext>()
                .InboxMessages.AnyAsync(m => m.Id == marker, ct));

        var inDefaultDatabase = await tenantScope.RunAsync(
            TestConstants.RootTenantId,
            (services, ct) => services.GetRequiredService<EventingDbContext>()
                .InboxMessages.AnyAsync(m => m.Id == marker, ct));

        inTenantDatabase.ShouldBeTrue("the work must have written to the tenant's own database");
        inDefaultDatabase.ShouldBeFalse("nothing must have been written to the default database");
    }

    private static async Task<T> WaitForAsync<T>(Func<T?> probe) where T : class
    {
        var deadline = DateTime.UtcNow + JobTimeout;
        while (DateTime.UtcNow < deadline)
        {
            var value = probe();
            if (value is not null)
            {
                return value;
            }

            await Task.Delay(200);
        }

        throw new TimeoutException($"The job did not complete within {JobTimeout}.");
    }
}
