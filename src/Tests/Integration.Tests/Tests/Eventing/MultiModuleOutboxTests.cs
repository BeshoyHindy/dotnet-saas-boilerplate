using Finbuckle.MultiTenant;
using Finbuckle.MultiTenant.Abstractions;
using Boilerplate.BuildingBlocks.Eventing.Inbox;
using Boilerplate.BuildingBlocks.Eventing.Outbox;
using Boilerplate.BuildingBlocks.Eventing.Persistence;
using Boilerplate.BuildingBlocks.Shared.Multitenancy;
using Boilerplate.Modules.Files.Contracts.Events;
using Boilerplate.Modules.Identity.Contracts.Events;
using Integration.Tests.Infrastructure;

namespace Integration.Tests.Tests.Eventing;

/// <summary>
/// Covers issue #1349: the outbox was registered once per module DbContext, non-keyed, so .NET DI
/// resolved whichever module registered last for the whole application — and only IdentityDbContext
/// mapped the tables, so every other module's publish hit a missing relation. These tests exercise
/// the real store against Postgres, with no substitution, so they fail if the single framework-owned
/// registration regresses.
/// </summary>
[Collection(AppCollectionDefinition.Name)]
public sealed class MultiModuleOutboxTests
{
    private readonly AppWebApplicationFactory _factory;

    public MultiModuleOutboxTests(AppWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Events_From_Multiple_Modules_Land_In_One_Framework_Outbox()
    {
        using var scope = await CreateTenantScopeAsync();
        var store = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
        var context = scope.ServiceProvider.GetRequiredService<EventingDbContext>();

        var filesEventId = Guid.CreateVersion7();
        var identityEventId = Guid.CreateVersion7();

        // Files first: under the old registration this is the publish that blew up with
        // "relation files.OutboxMessages does not exist" — or silently hijacked Identity's store.
        await store.AddAsync(NewFilesEvent(filesEventId));
        await store.AddAsync(NewIdentityEvent(identityEventId));

        var saved = await context.OutboxMessages
            .Where(m => m.Id == filesEventId || m.Id == identityEventId)
            .ToListAsync();

        saved.Select(m => m.Id).ShouldBe([filesEventId, identityEventId], ignoreOrder: true);
        saved.ShouldContain(
            m => m.Type.Contains(nameof(FileFinalizedIntegrationEvent), StringComparison.Ordinal),
            "a non-Identity module's event must be persisted, not dropped");
        saved.ShouldAllBe(m => m.ProcessedOnUtc == null && !m.IsDead);
    }

    [Fact]
    public async Task Application_Registers_Exactly_One_Outbox_And_Inbox_Store()
    {
        using var scope = await CreateTenantScopeAsync();

        scope.ServiceProvider.GetServices<IOutboxStore>().Count().ShouldBe(
            1,
            "a second IOutboxStore registration makes DI resolve the last one for every module (issue #1349)");
        scope.ServiceProvider.GetServices<IInboxStore>().Count().ShouldBe(
            1,
            "a second IInboxStore registration silently redirects idempotency writes (issue #1349)");
    }

    [Fact]
    public async Task Inbox_Idempotency_Writes_Land_In_The_Framework_Schema()
    {
        using var scope = await CreateTenantScopeAsync();
        var inbox = scope.ServiceProvider.GetRequiredService<IInboxStore>();
        var context = scope.ServiceProvider.GetRequiredService<EventingDbContext>();
        var eventId = Guid.CreateVersion7();

        (await inbox.HasProcessedAsync(eventId, "MultiModuleOutboxTests")).ShouldBeFalse();

        await inbox.MarkProcessedAsync(
            eventId,
            "MultiModuleOutboxTests",
            TestConstants.RootTenantId,
            nameof(FileFinalizedIntegrationEvent));

        (await inbox.HasProcessedAsync(eventId, "MultiModuleOutboxTests")).ShouldBeTrue();
        (await context.InboxMessages.CountAsync(m => m.Id == eventId)).ShouldBe(1);
    }

    [Fact]
    public async Task Identity_No_Longer_Owns_The_Outbox_Tables()
    {
        using var scope = await CreateTenantScopeAsync();
        var identity = scope.ServiceProvider.GetRequiredService<Boilerplate.Modules.Identity.Data.IdentityDbContext>();

        identity.Model.FindEntityType(typeof(OutboxMessage)).ShouldBeNull(
            "the outbox is framework infrastructure; a module mapping it again reintroduces the ambiguity");
        identity.Model.FindEntityType(typeof(InboxMessage)).ShouldBeNull();
    }

    private static FileFinalizedIntegrationEvent NewFilesEvent(Guid id) => new(
        id,
        DateTime.UtcNow,
        TestConstants.RootTenantId,
        $"corr-{id:N}",
        "Files",
        Guid.CreateVersion7(),
        "outbox-1349",
        Guid.CreateVersion7(),
        "text/plain",
        1024,
        1);

    private static UserRegisteredIntegrationEvent NewIdentityEvent(Guid id) => new(
        id,
        DateTime.UtcNow,
        TestConstants.RootTenantId,
        $"corr-{id:N}",
        "Identity",
        Guid.CreateVersion7().ToString(),
        "outbox-1349@example.com",
        "Out",
        "Box");

    private async Task<IServiceScope> CreateTenantScopeAsync()
    {
        var scope = _factory.Services.CreateScope();
        var tenant = await scope.ServiceProvider
            .GetRequiredService<IMultiTenantStore<AppTenantInfo>>()
            .GetAsync(TestConstants.RootTenantId);
        scope.ServiceProvider.GetRequiredService<IMultiTenantContextSetter>()
            .MultiTenantContext = new MultiTenantContext<AppTenantInfo>(tenant);
        return scope;
    }
}
