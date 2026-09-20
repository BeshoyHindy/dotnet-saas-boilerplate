using System.Collections.Concurrent;
using Boilerplate.BuildingBlocks.Persistence;
using Boilerplate.BuildingBlocks.Shared.Multitenancy;
using Finbuckle.MultiTenant.Abstractions;

namespace Integration.Tests.Infrastructure;

/// <summary>
/// A test-only <see cref="IDbInitializer"/> that fails the provisioning <c>Migrations</c> step for
/// a named tenant, on demand.
///
/// It is the fault-injection seam for the provisioning failure and retry suites. It replaces the
/// previous seam — a well-formed but unreachable per-tenant connection string, which stopped
/// existing when per-tenant databases were cut (#75). The throw lands in exactly the same place
/// the old one did: <c>ITenantService.MigrateTenantAsync</c> iterates every registered
/// <see cref="IDbInitializer"/> inside <c>ITenantScope.RunAsync</c>, so
/// <c>TenantProvisioningJob</c>'s catch converts it to
/// <c>MarkFailedAsync(..., Migrations, ...)</c> through the real production pipeline. No production
/// code is substituted.
///
/// Arming is explicit and per tenant (<see cref="Arm"/> / <see cref="Disarm"/>), so a retry test can
/// disarm between attempts and prove the second one succeeds. The <see cref="TenantIdPrefix"/>
/// guard is a safety net: a typo can never arm a tenant a neighbouring test owns.
/// </summary>
public sealed class FaultInjectingDbInitializer : IDbInitializer
{
    /// <summary>Only tenants whose id starts with this may be armed.</summary>
    public const string TenantIdPrefix = "provfail-";

    private static readonly ConcurrentDictionary<string, byte> ArmedTenants =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly IMultiTenantContextAccessor<AppTenantInfo> _accessor;

    public FaultInjectingDbInitializer(IMultiTenantContextAccessor<AppTenantInfo> accessor) =>
        _accessor = accessor;

    /// <summary>Makes the next <c>Migrations</c> step for <paramref name="tenantId"/> throw.</summary>
    public static void Arm(string tenantId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        if (!tenantId.StartsWith(TenantIdPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"Only a tenant id starting with '{TenantIdPrefix}' may be armed for injected failure.",
                nameof(tenantId));
        }

        ArmedTenants[tenantId] = 0;
    }

    /// <summary>Lets <paramref name="tenantId"/> migrate normally again.</summary>
    public static void Disarm(string tenantId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArmedTenants.TryRemove(tenantId, out _);
    }

    public Task MigrateAsync(CancellationToken cancellationToken)
    {
        var tenantId = _accessor.MultiTenantContext.TenantInfo?.Id;

        if (tenantId is not null && ArmedTenants.ContainsKey(tenantId))
        {
            throw new InvalidOperationException(
                $"Injected provisioning failure for tenant '{tenantId}' (FaultInjectingDbInitializer).");
        }

        return Task.CompletedTask;
    }

    public Task SeedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
