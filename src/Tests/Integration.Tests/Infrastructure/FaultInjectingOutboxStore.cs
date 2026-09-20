using System.Collections.Concurrent;
using Boilerplate.BuildingBlocks.Eventing.Abstractions;
using Boilerplate.BuildingBlocks.Eventing.Outbox;
using Boilerplate.Modules.Identity.Contracts.Events;

namespace Integration.Tests.Infrastructure;

/// <summary>
/// A test-only <see cref="IOutboxStore"/> decorator that fails the publish step of a registration,
/// on demand — the fault-injection seam for the registration-atomicity suite (#86).
///
/// It is the last step of <c>UserRegistrationService.RegisterAsync</c>, so a throw here stands in
/// for "anything after the user row is created blew up": the user, the role, the default groups and
/// the outbox row have all been written by then, and only a transaction around the whole of
/// registration can take them back.
///
/// The write is let through <b>first</b> and the throw comes after it on purpose. A seam that threw
/// before writing would prove nothing about the outbox row — it would simply never exist. Writing it
/// and then failing is what makes the assertion "no outbox row survives" a statement about the
/// ambient transaction the row joined (<c>.agents/rules/eventing.md</c> §Atomicity), which is the
/// half of the fix that cannot be seen from the HTTP response.
///
/// Arming is explicit and per e-mail address (<see cref="Arm"/> / <see cref="Disarm"/>), so a retry
/// test can disarm between attempts and prove the second one succeeds. The <see cref="EmailPrefix"/>
/// guard is a safety net: a typo can never arm a registration a neighbouring test owns.
/// </summary>
internal sealed class FaultInjectingOutboxStore : IOutboxStore
{
    /// <summary>Only a registration e-mail starting with this may be armed.</summary>
    public const string EmailPrefix = "regfail-";

    private static readonly ConcurrentDictionary<string, byte> ArmedEmails =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly EfCoreOutboxStore _inner;

    public FaultInjectingOutboxStore(EfCoreOutboxStore inner) => _inner = inner;

    /// <summary>Makes the next registration publish for <paramref name="email"/> throw.</summary>
    public static void Arm(string email)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);

        if (!email.StartsWith(EmailPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"Only a registration e-mail starting with '{EmailPrefix}' may be armed for injected failure.",
                nameof(email));
        }

        ArmedEmails[email] = 0;
    }

    /// <summary>Lets <paramref name="email"/> register normally again.</summary>
    public static void Disarm(string email)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArmedEmails.TryRemove(email, out _);
    }

    public async Task AddAsync(IIntegrationEvent @event, CancellationToken ct = default)
    {
        await _inner.AddAsync(@event, ct).ConfigureAwait(false);

        if (@event is UserRegisteredIntegrationEvent registered
            && !string.IsNullOrEmpty(registered.Email)
            && ArmedEmails.ContainsKey(registered.Email))
        {
            throw new InvalidOperationException(
                $"Injected registration failure for '{registered.Email}' (FaultInjectingOutboxStore).");
        }
    }

    public Task<IReadOnlyList<OutboxMessage>> ClaimBatchAsync(
        int batchSize, string claimedBy, TimeSpan lease, CancellationToken ct = default)
        => _inner.ClaimBatchAsync(batchSize, claimedBy, lease, ct);

    public Task MarkAsProcessedAsync(OutboxMessage message, CancellationToken ct = default)
        => _inner.MarkAsProcessedAsync(message, ct);

    public Task MarkAsFailedAsync(OutboxMessage message, string error, bool isDead, CancellationToken ct = default)
        => _inner.MarkAsFailedAsync(message, error, isDead, ct);

    public Task<IReadOnlyList<OutboxMessage>> GetDeadLetteredAsync(int max, CancellationToken ct = default)
        => _inner.GetDeadLetteredAsync(max, ct);

    public Task<int> RedriveDeadLettersAsync(IReadOnlyCollection<Guid>? ids, CancellationToken ct = default)
        => _inner.RedriveDeadLettersAsync(ids, ct);
}
