using Boilerplate.BuildingBlocks.Eventing.Abstractions;
using Boilerplate.BuildingBlocks.Shared.Multitenancy;
using Integration.Tests.Infrastructure;
using Integration.Tests.Tests.Jobs;

namespace Integration.Tests.Tests.Eventing;

/// <summary>
/// ADR-0002, "Jobs and events", for the event side: an integration event dispatched for tenant A
/// runs its handler under A and cannot read B's rows. Covered on both dispatch paths — the
/// in-memory bus directly, and the outbox, where the publisher's context is long gone by the time
/// the dispatcher picks the row up.
/// </summary>
[Collection(AppCollectionDefinition.Name)]
public sealed class EventTenantIsolationTests
{
    private readonly AppWebApplicationFactory _factory;
    private readonly TenantFixtures _tenants;

    public EventTenantIsolationTests(AppWebApplicationFactory factory)
    {
        _factory = factory;
        _tenants = new TenantFixtures(factory);
    }

    [Fact]
    public async Task InMemory_Dispatch_For_Tenant_A_Should_Not_See_Tenant_B_Rows()
    {
        var (tenantA, adminA) = await _tenants.CreateProvisionedTenantAsync("evta");
        var (_, adminB) = await _tenants.CreateProvisionedTenantAsync("evtb");

        var marker = Guid.CreateVersion7();

        using (var scope = _factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IEventBus>()
                .PublishAsync(NewEvent(marker, tenantA));
        }

        TenantProbeHandler.Observations.ShouldContainKey(marker, "the in-memory bus must dispatch synchronously");
        AssertIsolated(TenantProbeHandler.Observations[marker], tenantA, adminA, adminB);
    }

    [Fact]
    public async Task Outbox_Dispatch_For_Tenant_A_Should_Not_See_Tenant_B_Rows()
    {
        var (tenantA, adminA) = await _tenants.CreateProvisionedTenantAsync("outa");
        var (_, adminB) = await _tenants.CreateProvisionedTenantAsync("outb");

        var marker = Guid.CreateVersion7();

        // Published from inside the tenant, so the row is written to that tenant's database; the
        // dispatcher then picks it up with no ambient tenant of its own and has to restore A's from
        // the stored TenantId alone.
        var tenantScope = _factory.Services.GetRequiredService<ITenantScope>();
        await tenantScope.RunAsync(tenantA, async (services, ct) =>
            await services.GetRequiredService<IOutboxWriter>().AddAsync(NewEvent(marker, tenantA), ct));

        await OutboxDrain.DrainAsync(_factory.Services);

        TenantProbeHandler.Observations.ShouldContainKey(
            marker, "the outbox dispatcher must have delivered the event");
        AssertIsolated(TenantProbeHandler.Observations[marker], tenantA, adminA, adminB);
    }

    /// <summary>
    /// One dispatch pass delivers rows belonging to <b>different</b> tenants, each handler running
    /// under its own row's tenant.
    ///
    /// This is the shape the per-tenant-database cut (#75) left behind: the dispatcher used to run
    /// one pass per drain target, and now runs one pass, full stop, because outbox rows are
    /// <c>IGlobalEntity</c> with an explicit <c>TenantId</c> and there is one database. The failure
    /// it guards against is a claim narrowed to a single tenant — every other tenant's events
    /// strand, and the looping <see cref="OutboxDrain.DrainAsync"/> every other test uses would hide
    /// it by picking the rest up on the next pass. Hence <see cref="OutboxDrain.DispatchOnceAsync"/>,
    /// and hence the pre-drain: the backlog the shared suite database carries must not be what fills
    /// the batch.
    /// </summary>
    [Fact]
    public async Task One_Outbox_Pass_Should_Deliver_Rows_For_Two_Different_Tenants()
    {
        var (tenantA, adminA) = await _tenants.CreateProvisionedTenantAsync("onepassa");
        var (tenantB, adminB) = await _tenants.CreateProvisionedTenantAsync("onepassb");

        // Clear anything earlier tests left queued, so the single pass below is claiming our two
        // rows and not someone else's backlog.
        await OutboxDrain.DrainAsync(_factory.Services);

        var markerA = Guid.CreateVersion7();
        var markerB = Guid.CreateVersion7();

        var tenantScope = _factory.Services.GetRequiredService<ITenantScope>();
        await tenantScope.RunAsync(tenantA, async (services, ct) =>
            await services.GetRequiredService<IOutboxWriter>().AddAsync(NewEvent(markerA, tenantA), ct));
        await tenantScope.RunAsync(tenantB, async (services, ct) =>
            await services.GetRequiredService<IOutboxWriter>().AddAsync(NewEvent(markerB, tenantB), ct));

        // Exactly one pass — one ClaimBatchAsync, one publish loop.
        await OutboxDrain.DispatchOnceAsync(_factory.Services);

        TenantProbeHandler.Observations.ShouldContainKey(
            markerA, "one pass must deliver tenant A's row");
        TenantProbeHandler.Observations.ShouldContainKey(
            markerB, "one pass must deliver tenant B's row too — not on a later pass, this one");

        AssertIsolated(TenantProbeHandler.Observations[markerA], tenantA, adminA, adminB);
        AssertIsolated(TenantProbeHandler.Observations[markerB], tenantB, adminB, adminA);
    }

    /// <summary>
    /// The handler ran under <paramref name="expectedTenantId"/> and could read that tenant's rows
    /// and no other tenant's.
    /// </summary>
    private static void AssertIsolated(
        TenantObservation observation,
        string expectedTenantId,
        string ownAdminEmail,
        string otherTenantAdminEmail)
    {
        observation.TenantId.ShouldBe(expectedTenantId, "the handler must run under the event's tenant");
        observation.VisibleUserEmails.ShouldContain(ownAdminEmail);
        observation.VisibleUserEmails.ShouldNotContain(
            otherTenantAdminEmail,
            $"a handler running as {expectedTenantId} must not be able to read another tenant's rows");
    }

    private static TenantProbeIntegrationEvent NewEvent(Guid marker, string tenantId) => new(
        Guid.CreateVersion7(),
        DateTime.UtcNow,
        tenantId,
        $"corr-{marker:N}",
        "tests",
        marker);
}
