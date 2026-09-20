using Finbuckle.MultiTenant.Abstractions;
using Boilerplate.BuildingBlocks.Core.Common;
using Boilerplate.BuildingBlocks.Shared.Identity.Claims;
using Hangfire.Client;
using Hangfire.Logging;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Boilerplate.BuildingBlocks.Jobs;

/// <summary>
/// Stamps the enqueuing tenant (and user) onto every background job.
///
/// ADR-0002: the tenant comes from the <b>ambient tenant context</b>, not from
/// <c>HttpContext</c>. A job enqueued from a hosted service, a recurring trigger or an event
/// handler is just as tenant-bound as one enqueued from a request, and the old "no HttpContext →
/// skip the tenant" shortcut silently produced tenant-less jobs that then read the wrong rows.
///
/// Only the tenant <i>Id</i> is written. The record — connection string and all — is re-read from
/// the tenant store when the job runs (see <see cref="AppJobActivator"/>).
/// </summary>
public class AppJobFilter : IClientFilter
{
    private static readonly ILog Logger = LogProvider.GetCurrentClassLogger();

    private readonly IServiceProvider _services;

    public AppJobFilter(IServiceProvider services) => _services = services;

    public void OnCreating(CreatingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var job = context.Job;

        if (SystemJobs.IsSystemJob(job))
        {
            // Declared tenant-less. Never capture a tenant, even when one happens to be ambient —
            // a system job that wants tenant data must enter each tenant through ITenantScope.
            Logger.DebugFormat("Job {0} is marked [SystemJob]; no tenant captured.", SystemJobs.Describe(job));
        }
        else
        {
            context.SetJobParameter(JobParameterNames.TenantId, RequireAmbientTenantId(job));
        }

        var userId = ResolveUserId();
        if (!string.IsNullOrEmpty(userId))
        {
            context.SetJobParameter(QueryStringKeys.UserId, userId);
        }
    }

    public void OnCreated(CreatedContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        Logger.InfoFormat(
            "Job created with parameters {0}",
            context.Parameters.Select(x => x.Key + "=" + x.Value).DefaultIfEmpty("<none>")
                .Aggregate((s1, s2) => s1 + ";" + s2));
    }

    /// <summary>
    /// Fails loudly at the enqueue site rather than letting a tenant-less job reach the worker,
    /// where the failure would surface far from the code that caused it.
    /// </summary>
    private string RequireAmbientTenantId(Hangfire.Common.Job job)
    {
        var tenantId = _services.GetService<IMultiTenantContextAccessor>()?
            .MultiTenantContext?.TenantInfo?.Id;

        if (string.IsNullOrWhiteSpace(tenantId))
        {
            throw new InvalidOperationException(
                $"Job {SystemJobs.Describe(job)} was enqueued with no ambient tenant. Enqueue it inside a " +
                "request or an ITenantScope so the job runs under a tenant, or mark it [SystemJob] if it is " +
                "genuinely tenant-less (ADR-0002).");
        }

        return tenantId;
    }

    /// <summary>
    /// The current user is only used for audit stamping, and the ambient <c>ICurrentUser</c> is a
    /// scoped service we cannot reach from the root provider, so this still reads
    /// <c>HttpContext.User</c>. A background enqueue simply has no user, which is correct.
    /// </summary>
    private string? ResolveUserId() =>
        _services.GetService<IHttpContextAccessor>()?.HttpContext?.User.GetUserId();
}
