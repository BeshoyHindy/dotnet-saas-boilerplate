using Boilerplate.BuildingBlocks.Eventing;
using Boilerplate.BuildingBlocks.Eventing.Abstractions;
using Boilerplate.BuildingBlocks.Eventing.Outbox;
using Boilerplate.BuildingBlocks.Eventing.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Framework.Tests.Eventing;

public class OutboxDispatcherHostedServiceTests
{
    /// <summary>
    /// There is one database (#75) and outbox rows are <c>IGlobalEntity</c> carrying an explicit
    /// <c>TenantId</c>, so one cycle is one pass: one scope, one dispatcher, one claim.
    /// </summary>
    [Fact]
    public async Task One_Cycle_Drains_The_Outbox_Once()
    {
        var store = Substitute.For<IOutboxStore>();
        store.ClaimBatchAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<OutboxMessage>>([]));

        await using var root = BuildProvider(store);
        using var sut = CreateSut(root);

        await sut.DispatchOutboxAsync(CancellationToken.None);

        await store.Received(1).ClaimBatchAsync(
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// A cancelled cycle claims nothing: <c>ExecuteAsync</c>'s loop owns shutdown, and the pass must
    /// not open a scope on a token that is already done.
    /// </summary>
    [Fact]
    public async Task A_Cancelled_Cycle_Claims_Nothing()
    {
        var store = Substitute.For<IOutboxStore>();

        await using var root = BuildProvider(store);
        using var sut = CreateSut(root);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => sut.DispatchOutboxAsync(cts.Token));

        await store.DidNotReceive().ClaimBatchAsync(
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    private static ServiceProvider BuildProvider(IOutboxStore store)
    {
        var services = new ServiceCollection();
        services.AddSingleton(store);
        services.AddSingleton(Substitute.For<IEventBus>());
        services.AddSingleton<IEventSerializer, JsonEventSerializer>();
        services.AddSingleton(Options.Create(new EventingOptions()));
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
        services.AddScoped<OutboxDispatcher>();
        return services.BuildServiceProvider();
    }

    private static OutboxDispatcherHostedService CreateSut(IServiceProvider root) =>
        new(root.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new EventingOptions()),
            NullLogger<OutboxDispatcherHostedService>.Instance);
}
