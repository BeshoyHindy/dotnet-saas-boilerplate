namespace Boilerplate.BuildingBlocks.Jobs;

/// <summary>
/// Declares that a job is deliberately tenant-less: platform work that spans the whole
/// installation (outbox drains, retention sweeps, the tenant expiry scan, provisioning a tenant
/// that does not exist yet) rather than work done on behalf of one tenant.
///
/// ADR-0002 makes tenant-less background work an explicit decision instead of an accident:
/// <list type="bullet">
///   <item>a marked job never captures a tenant, even when one is ambient at enqueue time;</item>
///   <item>an <b>un</b>marked job enqueued with no ambient tenant throws at the enqueue site, and one
///     that somehow reaches the worker without a tenant parameter fails the job.</item>
/// </list>
/// A marked job that needs to touch tenant data must enter each tenant explicitly through
/// <c>ITenantScope</c>, which loads the full tenant record — connection string included.
///
/// Applies to the method Hangfire invokes, or to the whole job class.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class SystemJobAttribute : Attribute
{
}
