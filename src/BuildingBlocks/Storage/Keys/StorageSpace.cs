namespace Boilerplate.BuildingBlocks.Storage.Keys;

/// <summary>
/// Which of the block's two tenant-prefixed key spaces an object belongs to. The caller states the
/// intent; the block composes the physical key (<see cref="TenantStorageKeyRules"/>). There is no
/// third option and no un-prefixed space: ADR-0002 makes tenant prefixing the block's job, not a
/// caller convention.
/// </summary>
public enum StorageSpace
{
    /// <summary>
    /// <c>tenants/{tenantId}/…</c> — reachable only through a presigned URL or the API. The deploy
    /// stacks deliberately grant no anonymous read anywhere under it, because public and private
    /// Files objects share this space and visibility is a database column.
    /// </summary>
    Private = 0,

    /// <summary>
    /// <c>uploads/tenants/{tenantId}/…</c> — avatars and tenant brand assets, which are public by
    /// nature and are persisted as durable unsigned URLs. <c>uploads/</c> stays the outermost
    /// segment so the one anonymous-read grant in <c>deploy/</c> (contract-tested) keeps covering
    /// exactly this space and nothing else.
    /// </summary>
    Public = 1,
}
