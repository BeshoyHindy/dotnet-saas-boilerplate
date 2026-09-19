namespace Boilerplate.BuildingBlocks.Shared.Multitenancy;

/// <summary>
/// Thrown when <see cref="ITenantScope"/> is asked to enter a tenant the store does not know.
///
/// Deliberately not <c>NotFoundException</c>: this is never a caller's 404. It means a job, an
/// integration event or a drain pass named a tenant that has since been deleted, and the right
/// outcome is a loud failure on the background worker, not a silent tenant-less run.
/// </summary>
public sealed class UnknownTenantException : Exception
{
    public UnknownTenantException()
        : base("The tenant store does not contain the requested tenant.")
    {
    }

    public UnknownTenantException(string message)
        : base(message)
    {
    }

    public UnknownTenantException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public static UnknownTenantException ForTenant(string tenantId) =>
        new($"Tenant '{tenantId}' is not in the tenant store, so a tenant scope cannot be opened for it.");
}
