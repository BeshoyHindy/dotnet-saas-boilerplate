using Boilerplate.BuildingBlocks.Core.Common;
using Boilerplate.BuildingBlocks.Core.Context;
using Boilerplate.BuildingBlocks.Shared.Multitenancy;
using Hangfire;
using Hangfire.Server;
using Microsoft.Extensions.DependencyInjection;

namespace Boilerplate.BuildingBlocks.Jobs;

/// <summary>
/// Builds the DI scope a job runs in — under the tenant the job was enqueued for.
///
/// The tenant is opened through <see cref="ITenantScope"/>, so the full record is re-read from the
/// tenant store (a dedicated connection string survives) and installed <b>before</b> the DI scope
/// exists. Jobs are declared tenant-bound or <c>[SystemJob]</c>; there is no third, silent option:
/// an unmarked job that arrives without a tenant parameter fails the job rather than running
/// tenant-less against whatever database the default connection points at.
/// </summary>
public class AppJobActivator : JobActivator
{
    private readonly IServiceProvider _services;

    public AppJobActivator(IServiceProvider services) =>
        _services = services ?? throw new ArgumentNullException(nameof(services));

    public override JobActivatorScope BeginScope(PerformContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var job = context.BackgroundJob?.Job;

        if (SystemJobs.IsSystemJob(job))
        {
            // Declared tenant-less: a plain scope, no ambient tenant. Any tenant data it touches
            // must be reached through ITenantScope from inside the job.
            var scope = _services.CreateScope();
            return new Scope(context, scope.ServiceProvider, scope);
        }

        var tenantId = context.GetJobParameter<string>(JobParameterNames.TenantId);
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            throw new InvalidOperationException(
                $"Job {SystemJobs.Describe(job)} carries no tenant and is not marked [SystemJob]. It cannot be " +
                "run: a tenant-less run would read and write whatever the default connection points at " +
                "(ADR-0002).");
        }

        var handle = OpenTenant(tenantId, job);
        return new Scope(context, handle.Services, handle);
    }

    private ITenantScopeHandle OpenTenant(string tenantId, Hangfire.Common.Job? job)
    {
        var tenantScope = _services.GetService<ITenantScope>()
            ?? throw new InvalidOperationException(
                $"Job {SystemJobs.Describe(job)} is tenant-bound but no ITenantScope is registered. " +
                "Register the multitenancy module, or mark the job [SystemJob].");

        // Hangfire owns the scope lifetime through BeginScope/Dispose, so this is the one place the
        // blocking form of ITenantScope is used. Job workers have no synchronization context.
        var handle = tenantScope.BeginAsync(tenantId).GetAwaiter().GetResult();

        if (!handle.Tenant.IsActive)
        {
            handle.Dispose();

            // Fail closed. A deactivated tenant is one whose work must stop, and a job that keeps
            // writing for it after deactivation is exactly the leak deactivation is meant to prevent.
            throw new InvalidOperationException(
                $"Job {SystemJobs.Describe(job)} is bound to tenant '{tenantId}', which is deactivated. " +
                "Reactivate the tenant or delete the job.");
        }

        return handle;
    }

    private sealed class Scope : JobActivatorScope, IServiceProvider
    {
        private readonly PerformContext _context;
        private readonly IServiceProvider _services;
        private readonly IDisposable _owner;

        public Scope(PerformContext context, IServiceProvider services, IDisposable owner)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _services = services;
            _owner = owner;

            ReceiveParameters();
        }

        private void ReceiveParameters()
        {
            string userId = _context.GetJobParameter<string>(QueryStringKeys.UserId);
            if (!string.IsNullOrEmpty(userId))
            {
                _services.GetRequiredService<ICurrentUserInitializer>().SetCurrentUserId(userId);
            }
        }

        public override object Resolve(Type type) =>
            ActivatorUtilities.GetServiceOrCreateInstance(this, type);

        public override void DisposeScope() => _owner.Dispose();

        object? IServiceProvider.GetService(Type serviceType) =>
            serviceType == typeof(PerformContext)
                ? _context
                : _services.GetService(serviceType);
    }
}
