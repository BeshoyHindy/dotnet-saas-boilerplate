using System.Security.Claims;

namespace Integration.Tests.Infrastructure;

/// <summary>
/// Makes the external-sign-in race deterministic, entirely from the test side.
///
/// <para>The losing interleaving is narrow: a racer must read "no user with this address", and only
/// THEN have the winner commit, so that the create it goes on to make is refused by Identity's
/// pre-insert validators rather than by the index. Left to the thread pool that window opens perhaps
/// once in a hundred runs — which is exactly how it reached a full-suite run and nowhere else.</para>
///
/// <para>The seam is the principal itself. <c>GetOrCreateFromPrincipalAsync</c> reads the e-mail
/// claim, does its existence check, and only then reads the name claims to build the user — so a
/// <see cref="ClaimsPrincipal"/> that blocks inside <c>FindFirst(string)</c> for the given-name claim
/// holds a racer at precisely the point between those two steps. <see cref="ClaimsPrincipal"/>
/// declares that method virtual, so no production code is substituted, delayed or aware of this.</para>
///
/// <para>The first racer to arrive is let through and becomes the winner; the rest wait until
/// <see cref="ReleaseHeld"/>, which a test calls once the winner's sign-in has returned.</para>
/// </summary>
internal sealed class ExternalSignInGate : IDisposable
{
    private readonly ManualResetEventSlim _held = new(initialState: false);
    private readonly TimeSpan _timeout = TimeSpan.FromSeconds(30);
    private int _arrivals;

    /// <summary>Lets every racer waiting between the existence check and the create continue.</summary>
    public void ReleaseHeld() => _held.Set();

    /// <summary>
    /// A principal for <paramref name="email"/> that stops at the gate. No name claim, so the
    /// username is derived from the address — every racer for one address derives the same one.
    /// </summary>
    public ClaimsPrincipal PrincipalFor(string email) => new GatedPrincipal(
        new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Email, email),
                new Claim(ClaimTypes.GivenName, "Ext"),
                new Claim(ClaimTypes.Surname, "Auth"),
            ],
            authenticationType: "IntegrationTestExternalProvider"),
        this);

    private void PassOrWait()
    {
        // The first arrival is the winner and is never held: it has to be able to commit, because
        // its commit is the state every other racer must then collide with.
        if (Interlocked.Increment(ref _arrivals) == 1)
        {
            return;
        }

        if (!_held.Wait(_timeout))
        {
            throw new TimeoutException(
                "ExternalSignInGate was never released; the winning sign-in did not finish.");
        }
    }

    public void Dispose() => _held.Dispose();

    private sealed class GatedPrincipal : ClaimsPrincipal
    {
        private readonly ExternalSignInGate _gate;
        private int _stopped;

        public GatedPrincipal(ClaimsIdentity identity, ExternalSignInGate gate) : base(identity) =>
            _gate = gate;

        public override Claim? FindFirst(string type)
        {
            // The given-name claim is the first thing read when the user is being BUILT — i.e. after
            // the "does this address already have a user?" query has come back empty. Stop here once.
            if (string.Equals(type, ClaimTypes.GivenName, StringComparison.Ordinal)
                && Interlocked.Exchange(ref _stopped, 1) == 0)
            {
                _gate.PassOrWait();
            }

            return base.FindFirst(type);
        }
    }
}
