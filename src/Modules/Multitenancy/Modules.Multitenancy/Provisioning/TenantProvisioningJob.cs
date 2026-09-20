using Finbuckle.MultiTenant.Abstractions;
using Boilerplate.BuildingBlocks.Core.Exceptions;
using Boilerplate.BuildingBlocks.Jobs;
using Boilerplate.BuildingBlocks.Shared.Multitenancy;
using Boilerplate.Modules.Multitenancy.Contracts;
using Boilerplate.Modules.Multitenancy.Services;
using Microsoft.Extensions.Logging;

namespace Boilerplate.Modules.Multitenancy.Provisioning;

/// <summary>
/// Provisioning is named work <i>about</i> a tenant, not work <i>as</i> one: a root operator
/// enqueues it, the target tenant is an argument, and the bookkeeping it writes lives in the
/// untenanted tenant catalog. Hence <see cref="SystemJobAttribute"/>. The steps that write rows
/// belonging to the new tenant — seeding above all, since the shared schema is already at head —
/// enter it through <c>ITenantScope</c> inside <see cref="ITenantService"/>.
/// </summary>
[SystemJob]
public sealed class TenantProvisioningJob
{
    private readonly ITenantProvisioningService _provisioningService;
    private readonly IMultiTenantStore<AppTenantInfo> _tenantStore;
    private readonly ITenantService _tenantService;
    private readonly ILogger<TenantProvisioningJob> _logger;

    public TenantProvisioningJob(
        ITenantProvisioningService provisioningService,
        IMultiTenantStore<AppTenantInfo> tenantStore,
        ITenantService tenantService,
        ILogger<TenantProvisioningJob> logger)
    {
        _provisioningService = provisioningService;
        _tenantStore = tenantStore;
        _tenantService = tenantService;
        _logger = logger;
    }

    public async Task RunAsync(string tenantId, string correlationId, CancellationToken cancellationToken = default)
    {
        var tenant = await _tenantStore.GetAsync(tenantId).ConfigureAwait(false)
            ?? throw new NotFoundException($"Tenant {tenantId} not found during provisioning.");

        var currentStep = TenantProvisioningStepName.Database;
        try
        {
            var runDatabase = await _provisioningService.MarkRunningAsync(tenantId, correlationId, currentStep, cancellationToken).ConfigureAwait(false);

            if (runDatabase)
            {
                await _provisioningService.MarkStepCompletedAsync(tenantId, correlationId, currentStep, cancellationToken).ConfigureAwait(false);
            }

            currentStep = TenantProvisioningStepName.Migrations;
            var runMigrations = await _provisioningService.MarkRunningAsync(tenantId, correlationId, currentStep, cancellationToken).ConfigureAwait(false);
            if (runMigrations)
            {
                await _tenantService.MigrateTenantAsync(tenant, cancellationToken).ConfigureAwait(false);
                await _provisioningService.MarkStepCompletedAsync(tenantId, correlationId, currentStep, cancellationToken).ConfigureAwait(false);
            }

            currentStep = TenantProvisioningStepName.Seeding;
            var runSeeding = await _provisioningService.MarkRunningAsync(tenantId, correlationId, currentStep, cancellationToken).ConfigureAwait(false);
            if (runSeeding)
            {
                await _tenantService.SeedTenantAsync(tenant, cancellationToken).ConfigureAwait(false);
                await _provisioningService.MarkStepCompletedAsync(tenantId, correlationId, currentStep, cancellationToken).ConfigureAwait(false);
            }

            currentStep = TenantProvisioningStepName.CacheWarm;
            var runCacheWarm = await _provisioningService.MarkRunningAsync(tenantId, correlationId, currentStep, cancellationToken).ConfigureAwait(false);
            if (runCacheWarm)
            {
                await _provisioningService.MarkStepCompletedAsync(tenantId, correlationId, currentStep, cancellationToken).ConfigureAwait(false);
            }

            await _provisioningService.MarkCompletedAsync(tenantId, correlationId, cancellationToken).ConfigureAwait(false);

            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("Provisioned tenant {TenantId} correlation {CorrelationId}", tenantId, correlationId);
            }
        }
        // Broad catch is intentional: any provisioning failure must be recorded in the provisioning
        // log before re-throwing so the job framework can handle retry/dead-letter.
        catch (Exception ex)
        {
            _logger.LogError(ex, "Provisioning failed for tenant {TenantId}", tenantId);
            await _provisioningService.MarkFailedAsync(tenantId, correlationId, currentStep, ex.Message, cancellationToken).ConfigureAwait(false);
            throw;
        }
    }
}