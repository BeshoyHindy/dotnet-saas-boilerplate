using Boilerplate.BuildingBlocks.Core.Exceptions;

namespace Boilerplate.BuildingBlocks.Storage.Keys;

/// <summary>
/// The ambient tenant named a storage object it does not own — a key outside both
/// <c>tenants/{tenantId}/…</c> and <c>uploads/tenants/{tenantId}/…</c>, or one that is not a
/// well-formed key at all. Thrown <b>before</b> any call reaches the storage backend.
///
/// <para><b>404, not 403.</b> It derives from <see cref="NotFoundException"/> so the HTTP paths
/// answer exactly as they do for a row that isn't there: a 403 would confirm the object exists and
/// put tenant isolation behind a permission, which is the failure mode the tenant endpoint sweep
/// exists to catch. The message is deliberately the same as any other miss.</para>
///
/// <para>Also raised for a stale handle — a pre-#78 flat <c>uploads/{type}/…</c> key still sitting
/// in a development database, say. Deletion paths that must survive one use
/// <c>IStorageService.RemoveIfOwnedAsync</c>, which skips and logs instead.</para>
/// </summary>
public sealed class StorageKeyNotOwnedException : NotFoundException
{
    private const string DefaultMessage = "storage object not found";

    public StorageKeyNotOwnedException()
        : base(DefaultMessage)
    {
    }

    public StorageKeyNotOwnedException(string message)
        : base(message)
    {
    }

    public StorageKeyNotOwnedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
