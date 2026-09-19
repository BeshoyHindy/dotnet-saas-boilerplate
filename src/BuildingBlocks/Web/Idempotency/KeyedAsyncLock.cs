namespace Boilerplate.BuildingBlocks.Web.Idempotency;

/// <summary>
/// A per-key async mutex, used by <see cref="IdempotencyEndpointFilter"/> so two simultaneous
/// requests carrying the same Idempotency-Key do not both run the handler.
/// </summary>
/// <remarks>
/// <para>
/// <b>This covers one process, and that is all it claims.</b> <c>IDistributedCache</c> has no
/// set-if-absent, so there is no cheap way to make "first writer wins" hold across instances; on a
/// multi-instance deployment two duplicates that land on different instances can still both execute,
/// with the later write winning the entry. Documented in <c>.agents/rules/security.md</c> rather than
/// papered over — a Redis <c>SET NX</c> lease is the fix if that window ever matters, and it needs
/// the multiplexer, not this abstraction.
/// </para>
/// <para>
/// Entries are reference-counted and removed when the last waiter leaves, so a process that sees a
/// million distinct keys does not keep a million semaphores. Stripe-hashing would be simpler but
/// would let one slow handler block an unrelated request that happened to collide on a stripe.
/// </para>
/// </remarks>
internal sealed class KeyedAsyncLock
{
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    /// <summary>
    /// Waits for exclusive use of <paramref name="key"/>. Dispose the returned handle to release it.
    /// </summary>
    public async ValueTask<IDisposable> AcquireAsync(string key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);

        Entry entry;
        lock (_entries)
        {
            if (!_entries.TryGetValue(key, out var existing))
            {
                existing = new Entry();
                _entries[key] = existing;
            }

            existing.Waiters++;
            entry = existing;
        }

        try
        {
            await entry.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Release(key, entry, held: false);
            throw;
        }

        return new Handle(this, key, entry);
    }

    private void Release(string key, Entry entry, bool held)
    {
        if (held)
        {
            entry.Semaphore.Release();
        }

        lock (_entries)
        {
            entry.Waiters--;
            if (entry.Waiters == 0 && _entries.TryGetValue(key, out var current) && ReferenceEquals(current, entry))
            {
                _entries.Remove(key);
                entry.Semaphore.Dispose();
            }
        }
    }

    private sealed class Entry
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        /// <summary>Holder + waiters. Mutated only under the dictionary lock.</summary>
        public int Waiters { get; set; }
    }

    private sealed class Handle(KeyedAsyncLock owner, string key, Entry entry) : IDisposable
    {
        private bool _released;

        public void Dispose()
        {
            if (_released)
            {
                return;
            }

            _released = true;
            owner.Release(key, entry, held: true);
        }
    }
}
