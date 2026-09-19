namespace Boilerplate.BuildingBlocks.Jobs;

/// <summary>
/// Hangfire job-parameter keys. <see cref="Tenant"/> spells the same word as the retired
/// <c>tenant</c> request header but has nothing to do with it: it names a serialized
/// <c>AppTenantInfo</c> stored on the job record at enqueue time and read back by
/// <c>AppJobActivator</c>. Keeping the literal unchanged keeps already-enqueued jobs readable.
/// </summary>
internal static class JobParameterNames
{
    public const string Tenant = "tenant";
}
