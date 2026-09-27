using Boilerplate.BuildingBlocks.Shared.Multitenancy;
using Boilerplate.Modules.Identity.Services;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Identity.Tests.Services;

/// <summary>
/// <see cref="SessionLiveness"/> is what makes a revoked session stop working on the next request
/// rather than when the access token expires (#118). These tests pin the answer for each state a
/// session row can be in, that the answer is cached for <see cref="SessionLiveness.CacheDuration"/>
/// and no longer, and that a revocation on this instance wins over the cache immediately — including
/// over a lookup that was already in flight when the revocation landed.
/// </summary>
public sealed class SessionLivenessTests : IDisposable
{
    private const string TenantA = "alpha";
    private const string TenantB = "beta";

    private static readonly DateTimeOffset Start = new(2026, 1, 15, 10, 0, 0, TimeSpan.Zero);

    private readonly ITenantScope _tenantScope = Substitute.For<ITenantScope>();
    private readonly MutableTimeProvider _time = new(Start);
    private readonly SessionLiveness _sut;
    private int _storeReads;

    public SessionLivenessTests()
    {
        _sut = new SessionLiveness(_tenantScope, _time);
    }

    public void Dispose() => _sut.Dispose();

    /// <summary>Makes every store read (a tenant-scoped lookup) answer <paramref name="state"/>.</summary>
    private void StoreReturns(SessionState? state, Action? onRead = null)
    {
        _tenantScope
            .RunAsync(
                Arg.Any<string>(),
                Arg.Any<Func<IServiceProvider, CancellationToken, Task<SessionState?>>>(),
                Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                Interlocked.Increment(ref _storeReads);
                onRead?.Invoke();
                return Task.FromResult(state);
            });
    }

    private static SessionState Active(DateTimeOffset now) => new(IsRevoked: false, ExpiresAt: now.UtcDateTime.AddDays(7));

    #region Happy Path

    [Fact]
    public async Task IsLiveAsync_Should_ReturnTrue_When_SessionIsActive()
    {
        StoreReturns(Active(Start));

        (await _sut.IsLiveAsync(TenantA, Guid.NewGuid(), CancellationToken.None)).ShouldBeTrue();
    }

    [Fact]
    public async Task IsLiveAsync_Should_LookUpTheSession_InsideTheTokensTenant()
    {
        StoreReturns(Active(Start));
        using var cts = new CancellationTokenSource();

        await _sut.IsLiveAsync(TenantA, Guid.NewGuid(), cts.Token);

        await _tenantScope.Received(1).RunAsync(
            TenantA,
            Arg.Any<Func<IServiceProvider, CancellationToken, Task<SessionState?>>>(),
            cts.Token);
    }

    #endregion

    #region Rejections

    [Fact]
    public async Task IsLiveAsync_Should_ReturnFalse_When_SessionIsRevoked()
    {
        StoreReturns(new SessionState(IsRevoked: true, ExpiresAt: Start.UtcDateTime.AddDays(7)));

        (await _sut.IsLiveAsync(TenantA, Guid.NewGuid(), CancellationToken.None)).ShouldBeFalse();
    }

    [Fact]
    public async Task IsLiveAsync_Should_ReturnFalse_When_SessionHasExpired()
    {
        StoreReturns(new SessionState(IsRevoked: false, ExpiresAt: Start.UtcDateTime.AddSeconds(-1)));

        (await _sut.IsLiveAsync(TenantA, Guid.NewGuid(), CancellationToken.None)).ShouldBeFalse();
    }

    [Fact]
    public async Task IsLiveAsync_Should_ReturnFalse_When_SessionIsUnknown()
    {
        // No row: deleted by the cleanup sweep, or a sid this tenant never issued.
        StoreReturns(null);

        (await _sut.IsLiveAsync(TenantA, Guid.NewGuid(), CancellationToken.None)).ShouldBeFalse();
    }

    [Fact]
    public async Task IsLiveAsync_Should_ReturnFalse_When_TenantIsUnknown()
    {
        _tenantScope
            .RunAsync(
                Arg.Any<string>(),
                Arg.Any<Func<IServiceProvider, CancellationToken, Task<SessionState?>>>(),
                Arg.Any<CancellationToken>())
            .ThrowsAsync(UnknownTenantException.ForTenant("ghost"));

        (await _sut.IsLiveAsync("ghost", Guid.NewGuid(), CancellationToken.None)).ShouldBeFalse();
    }

    #endregion

    #region Caching

    [Fact]
    public async Task IsLiveAsync_Should_NotReadTheStoreAgain_When_CalledWithinTheCacheDuration()
    {
        StoreReturns(Active(Start));
        var sessionId = Guid.NewGuid();

        await _sut.IsLiveAsync(TenantA, sessionId, CancellationToken.None);
        _time.Advance(SessionLiveness.CacheDuration - TimeSpan.FromSeconds(1));
        (await _sut.IsLiveAsync(TenantA, sessionId, CancellationToken.None)).ShouldBeTrue();

        _storeReads.ShouldBe(1);
    }

    [Fact]
    public async Task IsLiveAsync_Should_CacheADeadAnswer_Too()
    {
        // A revoked token replayed in a loop must not become a database read per request.
        StoreReturns(null);
        var sessionId = Guid.NewGuid();

        await _sut.IsLiveAsync(TenantA, sessionId, CancellationToken.None);
        (await _sut.IsLiveAsync(TenantA, sessionId, CancellationToken.None)).ShouldBeFalse();

        _storeReads.ShouldBe(1);
    }

    [Fact]
    public async Task IsLiveAsync_Should_ReadTheStoreAgain_When_TheCacheDurationHasPassed()
    {
        // This is the bound on every *other* instance: a revocation they did not see is picked up
        // once their cached answer lapses.
        StoreReturns(Active(Start));
        var sessionId = Guid.NewGuid();
        await _sut.IsLiveAsync(TenantA, sessionId, CancellationToken.None);

        StoreReturns(new SessionState(IsRevoked: true, ExpiresAt: Start.UtcDateTime.AddDays(7)));
        _time.Advance(SessionLiveness.CacheDuration);

        (await _sut.IsLiveAsync(TenantA, sessionId, CancellationToken.None)).ShouldBeFalse();
        _storeReads.ShouldBe(2);
    }

    [Fact]
    public async Task IsLiveAsync_Should_ReturnFalse_When_ACachedSessionExpiresWithinTheCacheDuration()
    {
        var expiresAt = Start.UtcDateTime.AddSeconds(5);
        StoreReturns(new SessionState(IsRevoked: false, ExpiresAt: expiresAt));
        var sessionId = Guid.NewGuid();
        (await _sut.IsLiveAsync(TenantA, sessionId, CancellationToken.None)).ShouldBeTrue();

        _time.Advance(TimeSpan.FromSeconds(5));

        (await _sut.IsLiveAsync(TenantA, sessionId, CancellationToken.None)).ShouldBeFalse();
        _storeReads.ShouldBe(1);
    }

    [Fact]
    public async Task IsLiveAsync_Should_KeepTenantsApart_When_TheSessionIdIsTheSame()
    {
        StoreReturns(Active(Start));
        var sessionId = Guid.NewGuid();

        await _sut.IsLiveAsync(TenantA, sessionId, CancellationToken.None);
        await _sut.IsLiveAsync(TenantB, sessionId, CancellationToken.None);

        _storeReads.ShouldBe(2);
        await _tenantScope.Received(1).RunAsync(
            TenantB,
            Arg.Any<Func<IServiceProvider, CancellationToken, Task<SessionState?>>>(),
            Arg.Any<CancellationToken>());
    }

    #endregion

    #region Revocation on this instance

    [Fact]
    public async Task MarkRevoked_Should_FailTheNextCheck_WithoutWaitingForTheCache()
    {
        StoreReturns(Active(Start));
        var sessionId = Guid.NewGuid();
        (await _sut.IsLiveAsync(TenantA, sessionId, CancellationToken.None)).ShouldBeTrue();

        _sut.MarkRevoked(TenantA, sessionId);

        (await _sut.IsLiveAsync(TenantA, sessionId, CancellationToken.None)).ShouldBeFalse();
    }

    [Fact]
    public async Task MarkRevoked_Should_OnlyAffectTheNamedTenantsSession()
    {
        StoreReturns(Active(Start));
        var sessionId = Guid.NewGuid();
        await _sut.IsLiveAsync(TenantB, sessionId, CancellationToken.None);

        _sut.MarkRevoked(TenantA, sessionId);

        (await _sut.IsLiveAsync(TenantB, sessionId, CancellationToken.None)).ShouldBeTrue();
    }

    [Fact]
    public async Task MarkRevoked_Should_Win_Over_ALookupThatWasAlreadyInFlight()
    {
        // The race: a request misses the cache and reads the row *before* the revoke commits; the
        // revoke then commits and marks the session; the request finishes and would cache the live
        // row it read. That stale "live" must not overwrite the revocation.
        var sessionId = Guid.NewGuid();
        StoreReturns(Active(Start), onRead: () => _sut.MarkRevoked(TenantA, sessionId));

        (await _sut.IsLiveAsync(TenantA, sessionId, CancellationToken.None)).ShouldBeFalse();
        (await _sut.IsLiveAsync(TenantA, sessionId, CancellationToken.None)).ShouldBeFalse();
    }

    #endregion

    /// <summary>A clock the test moves by hand, so cache lapse and session expiry are deterministic.</summary>
    private sealed class MutableTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }
}
