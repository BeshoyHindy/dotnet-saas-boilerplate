namespace Boilerplate.BuildingBlocks.Storage.Keys;

/// <summary>
/// A storage operation ran with no ambient tenant (or with an ambient tenant whose id is not a
/// slug). There is no fallback and no un-prefixed key space to fall back <i>to</i>: work that is
/// genuinely tenant-less has to enter a tenant through <c>ITenantScope</c> first, exactly as the
/// Files purge jobs do.
///
/// <para>Deliberately not a <c>CustomException</c>: this is a plumbing fault in the caller, not an
/// answer for a client, so it surfaces as a 500 with the usual generic body rather than as a status
/// an attacker could provoke and read.</para>
/// </summary>
public sealed class MissingStorageTenantException : InvalidOperationException
{
    private const string DefaultMessage =
        "No ambient tenant: every storage object key is tenant-prefixed by the Storage block (ADR-0002). " +
        "Enter the tenant through ITenantScope before touching storage.";

    public MissingStorageTenantException()
        : base(DefaultMessage)
    {
    }

    public MissingStorageTenantException(string message)
        : base(message)
    {
    }

    public MissingStorageTenantException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
