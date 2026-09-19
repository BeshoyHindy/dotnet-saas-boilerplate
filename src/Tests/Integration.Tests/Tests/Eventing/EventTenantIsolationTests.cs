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

    private static void AssertIsolated(TenantObservation observation, string tenantA, string adminA, string adminB)
    {
        observation.TenantId.ShouldBe(tenantA, "the handler must run under the event's tenant");
        observation.VisibleUserEmails.ShouldContain(adminA);
        observation.VisibleUserEmails.ShouldNotContain(
            adminB, "a handler running as tenant A must not be able to read tenant B's rows");
    }

    private static TenantProbeIntegrationEvent NewEvent(Guid marker, string tenantId) => new(
        Guid.CreateVersion7(),
        DateTime.UtcNow,
        tenantId,
        $"corr-{marker:N}",
        "tests",
        marker);
}
