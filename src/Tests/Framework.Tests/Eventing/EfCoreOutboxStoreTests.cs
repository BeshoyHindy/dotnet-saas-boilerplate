using Boilerplate.BuildingBlocks.Eventing;
using Boilerplate.BuildingBlocks.Eventing.Abstractions;
using Boilerplate.BuildingBlocks.Eventing.Outbox;
using Boilerplate.BuildingBlocks.Eventing.Serialization;
using Boilerplate.BuildingBlocks.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Framework.Tests.Eventing;

public class EfCoreOutboxStoreTests
{
    private sealed record SampleEvent(
        Guid Id,
        DateTime OccurredOnUtc,
        string? TenantId,
        string CorrelationId,
        string Source) : IIntegrationEvent;

    [Fact]
    public async Task AddAsync_Persists_Message_With_Event_Identity()
    {
        await using var context = EventingTestContext.CreateSqlite();
        var store = new EfCoreOutboxStore(
            context,
            new JsonEventSerializer(),
            NullLogger<EfCoreOutboxStore>.Instance,
            TimeProvider.System,
            Options.Create(new EventingOptions()),
            new AmbientDbTransactionRegistry());

        var evt = new SampleEvent(
            Guid.CreateVersion7(),
            new DateTime(2026, 8, 6, 12, 0, 0, DateTimeKind.Utc),
            "acme",
            "corr-1",
            "Tests");

        await store.AddAsync(evt, CancellationToken.None);

        var saved = context.OutboxMessages.Single();
        saved.Id.ShouldBe(evt.Id);
        saved.TenantId.ShouldBe("acme");
        saved.CorrelationId.ShouldBe("corr-1");
        saved.ProcessedOnUtc.ShouldBeNull();
        saved.IsDead.ShouldBeFalse();
        saved.Type.ShouldContain(nameof(SampleEvent));
    }
}
