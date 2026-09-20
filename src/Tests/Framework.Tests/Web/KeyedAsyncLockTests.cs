using Boilerplate.BuildingBlocks.Web.Idempotency;

namespace Framework.Tests.Web;

/// <summary>
/// The per-key mutex behind the idempotency filter. What matters here is not that it excludes — a
/// semaphore does that — but the two things that are easy to get wrong: a bounded wait that returns
/// rather than throws, and a dictionary that does not grow one semaphore per key it has ever seen.
/// </summary>
public sealed class KeyedAsyncLockTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(50);

    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task A_Second_Waiter_Should_Be_Excluded_While_TheFirst_Holds_TheKey()
    {
        var locks = new KeyedAsyncLock();

        using var held = await locks.TryAcquireAsync("k", Generous, CancellationToken.None);
        held.ShouldNotBeNull();

        (await locks.TryAcquireAsync("k", Timeout, CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task A_Different_Key_Should_Not_Wait_At_All()
    {
        // Per key, not striped: one slow handler must not block an unrelated request that happened
        // to hash the same way.
        var locks = new KeyedAsyncLock();

        using var held = await locks.TryAcquireAsync("k", Generous, CancellationToken.None);
        using var other = await locks.TryAcquireAsync("other", Timeout, CancellationToken.None);

        other.ShouldNotBeNull();
    }

    [Fact]
    public async Task Releasing_Should_Hand_TheKey_To_TheNext_Waiter()
    {
        var locks = new KeyedAsyncLock();

        var held = await locks.TryAcquireAsync("k", Generous, CancellationToken.None);
        held.ShouldNotBeNull();
        held.Dispose();

        using var next = await locks.TryAcquireAsync("k", Timeout, CancellationToken.None);
        next.ShouldNotBeNull();
    }

    [Fact]
    public async Task A_TimedOut_Waiter_Should_Not_Leak_TheEntry()
    {
        // The waiter that gives up still holds a reference to the entry. Dropping it the same way a
        // cancellation does is what keeps a process that sees a million keys from keeping a million
        // semaphores.
        var locks = new KeyedAsyncLock();

        var held = await locks.TryAcquireAsync("k", Generous, CancellationToken.None);
        held.ShouldNotBeNull();
        (await locks.TryAcquireAsync("k", Timeout, CancellationToken.None)).ShouldBeNull();

        locks.TrackedKeys.ShouldBe(1, "the holder is still there.");
        held.Dispose();
        locks.TrackedKeys.ShouldBe(0, "the last reference out removes the entry.");
    }

    [Fact]
    public async Task A_Cancelled_Waiter_Should_Throw_And_Not_Leak_TheEntry()
    {
        // Cancellation is the request going away, which is a different thing from the wait expiring:
        // it throws, because there is no caller left to run the handler for.
        var locks = new KeyedAsyncLock();
        using var cts = new CancellationTokenSource();

        var held = await locks.TryAcquireAsync("k", Generous, CancellationToken.None);
        held.ShouldNotBeNull();

        var waiting = locks.TryAcquireAsync("k", Generous, cts.Token).AsTask();
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(waiting);

        held.Dispose();
        locks.TrackedKeys.ShouldBe(0);
    }

    [Fact]
    public async Task Disposing_TheHandle_Twice_Should_Release_Once()
    {
        // The filter disposes through `using`; a double dispose must not add a permit and let two
        // holders in at once.
        var locks = new KeyedAsyncLock();

        var held = await locks.TryAcquireAsync("k", Generous, CancellationToken.None);
        held.ShouldNotBeNull();
        held.Dispose();
        held.Dispose();

        using var first = await locks.TryAcquireAsync("k", Timeout, CancellationToken.None);
        first.ShouldNotBeNull();
        (await locks.TryAcquireAsync("k", Timeout, CancellationToken.None)).ShouldBeNull();
    }
}
