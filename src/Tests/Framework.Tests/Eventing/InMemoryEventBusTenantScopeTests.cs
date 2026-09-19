using Boilerplate.BuildingBlocks.Eventing.Abstractions;
using Boilerplate.BuildingBlocks.Eventing.InMemory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Framework.Tests.Eventing;

/// <summary>
/// Guards the systemic fix for the background-dispatch tenant-context bug: handlers are resolved
/// from the scope the <see cref="IEventTenantScope"/> hands back, which was created after the
/// event's tenant was installed. Handlers resolved from a scope opened first materialize
/// tenant-filtered DbContexts with a null tenant and the default connection string.
///
/// Also pins ADR-0002's other half for events: tenant-less dispatch has to be declared
/// (<see cref="IGlobalIntegrationEvent"/>), never inferred from a null field.
/// </summary>
public sealed class InMemoryEventBusTenantScopeTests
{
    [Fact]
    public async Task PublishAsync_Should_BeginTenantScope_WithEventTenantId_WhileHandlerRuns()
    {
        // Arrange
        var scope = new RecordingTenantScope();
        var handler = new TenantProbingHandler(scope);

        var services = new ServiceCollection();
        services.AddSingleton<IIntegrationEventHandler<TenantScopedEvent>>(handler);
        scope.Services = services.BuildServiceProvider();

        var bus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance, scope);

        // Act
        await bus.PublishAsync(new TenantScopedEvent("acme"));

        // Assert — scope begun with the event's tenant, and it was still active when the
        // handler executed (i.e. before resolution, restored after).
        scope.BegunWith.ShouldHaveSingleItem().ShouldBe("acme");
        handler.ScopeWasActiveDuringHandle.ShouldBeTrue();
        scope.IsActive.ShouldBeFalse("the scope must be disposed once dispatch completes");
    }

    [Fact]
    public async Task PublishAsync_Should_Resolve_Handlers_From_The_TenantScopes_Provider()
    {
        var scope = new RecordingTenantScope();
        var handler = new TenantProbingHandler(scope);

        var services = new ServiceCollection();
        services.AddSingleton<IIntegrationEventHandler<TenantScopedEvent>>(handler);
        scope.Services = services.BuildServiceProvider();

        var bus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance, scope);

        await bus.PublishAsync(new TenantScopedEvent("acme"));

        handler.Handled.ShouldBeTrue(
            "the bus must resolve handlers from the tenant scope's provider — resolving from its own " +
            "would build them before the tenant exists");
    }

    [Fact]
    public async Task PublishAsync_Should_Throw_When_A_TenantLess_Event_Is_Not_Declared_Global()
    {
        var scope = new RecordingTenantScope { Services = new ServiceCollection().BuildServiceProvider() };
        var bus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance, scope);

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => bus.PublishAsync(new TenantScopedEvent(null)));

        ex.Message.ShouldContain("IGlobalIntegrationEvent");
        scope.BegunWith.ShouldBeEmpty("dispatch must not start at all");
    }

    [Fact]
    public async Task PublishAsync_Should_Allow_A_Declared_Global_Event_Without_A_Tenant()
    {
        var scope = new RecordingTenantScope();
        var handler = new GlobalHandler();

        var services = new ServiceCollection();
        services.AddSingleton<IIntegrationEventHandler<PlatformWideEvent>>(handler);
        scope.Services = services.BuildServiceProvider();

        var bus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance, scope);

        await bus.PublishAsync(new PlatformWideEvent());

        handler.Handled.ShouldBeTrue();
        scope.BegunWith.ShouldHaveSingleItem().ShouldBeNull("a global event enters no tenant");
    }

    #region Test doubles

    private sealed record TenantScopedEvent(string? TenantId) : IIntegrationEvent
    {
        public Guid Id { get; } = Guid.CreateVersion7();
        public DateTime OccurredOnUtc { get; } = DateTime.UtcNow;
        public string CorrelationId { get; } = Guid.CreateVersion7().ToString();
        public string Source { get; } = "tests";
    }

    private sealed record PlatformWideEvent : IGlobalIntegrationEvent
    {
        public Guid Id { get; } = Guid.CreateVersion7();
        public DateTime OccurredOnUtc { get; } = DateTime.UtcNow;
        public string? TenantId => null;
        public string CorrelationId { get; } = Guid.CreateVersion7().ToString();
        public string Source { get; } = "tests";
    }

    private sealed class RecordingTenantScope : IEventTenantScope
    {
        public List<string?> BegunWith { get; } = [];
        public bool IsActive { get; private set; }
        public IServiceProvider Services { get; set; } = default!;

        public Task<IEventTenantScopeHandle> BeginAsync(string? tenantId, CancellationToken cancellationToken = default)
        {
            BegunWith.Add(tenantId);
            IsActive = true;
            return Task.FromResult<IEventTenantScopeHandle>(new Handle(this));
        }

        private sealed class Handle(RecordingTenantScope owner) : IEventTenantScopeHandle
        {
            public IServiceProvider Services => owner.Services;

            public void Dispose() => owner.IsActive = false;
        }
    }

    private sealed class TenantProbingHandler(RecordingTenantScope scope)
        : IIntegrationEventHandler<TenantScopedEvent>
    {
        public bool ScopeWasActiveDuringHandle { get; private set; }

        public bool Handled { get; private set; }

        public Task HandleAsync(TenantScopedEvent @event, CancellationToken ct = default)
        {
            ScopeWasActiveDuringHandle = scope.IsActive;
            Handled = true;
            return Task.CompletedTask;
        }
    }

    private sealed class GlobalHandler : IIntegrationEventHandler<PlatformWideEvent>
    {
        public bool Handled { get; private set; }

        public Task HandleAsync(PlatformWideEvent @event, CancellationToken ct = default)
        {
            Handled = true;
            return Task.CompletedTask;
        }
    }

    #endregion
}
