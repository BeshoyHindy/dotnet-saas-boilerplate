namespace Boilerplate.BuildingBlocks.Jobs;

/// <summary>
/// Hangfire job-parameter keys.
/// </summary>
internal static class JobParameterNames
{
    /// <summary>
    /// The enqueuing tenant's immutable Id — a string, and nothing else.
    ///
    /// Replaces the retired <c>tenant</c> key, which stored the whole serialized
    /// <c>AppTenantInfo</c>: that put every tenant's database connection string (credentials
    /// included) into Hangfire's job storage, and pinned the job to a snapshot of the tenant taken
    /// at enqueue time. The record is now re-read from the tenant store when the job runs.
    /// The old key is deleted rather than read as a fallback — there are no deployed jobs.
    /// </summary>
    public const string TenantId = "tenantId";
}
