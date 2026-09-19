using Finbuckle.MultiTenant.Abstractions;
using Boilerplate.BuildingBlocks.Eventing.Abstractions;
using Boilerplate.BuildingBlocks.Eventing.Inbox;
using Boilerplate.BuildingBlocks.Jobs;
using Boilerplate.BuildingBlocks.Shared.Multitenancy;
using Boilerplate.Modules.Identity.Data;
using Hangfire;
using System.Collections.Concurrent;

namespace Integration.Tests.Tests.Jobs;

/// <summary>
/// What a unit of background work saw: the tenant that was ambient while it ran, the connection
/// string that tenant carried (empty = the default database), and the tenant-filtered rows it could
/// actually read.
/// </summary>
public sealed record TenantObservation(string? TenantId, string? ConnectionString, IReadOnlyList<string> VisibleUserEmails);

/// <summary>
/// A tenant-bound job. Unmarked on purpose: it must be enqueued under a tenant and must run under
/// that tenant. It also writes one inbox row keyed by the marker, which is the evidence for "the
/// work landed in the tenant's own database" — that row exists in exactly one database.
/// </summary>
[AutomaticRetry(Attempts = 0)]
public sealed class TenantProbeJob(
    IMultiTenantContextAccessor<AppTenantInfo> accessor,
    IdentityDbContext identity,
    IInboxStore inbox)
{
    public static readonly ConcurrentDictionary<Guid, TenantObservation> Observations = new();

    public async Task RunAsync(Guid marker, CancellationToken cancellationToken)
    {
        var tenant = accessor.MultiTenantContext.TenantInfo;

        var emails = await identity.Users
            .Select(u => u.Email!)
            .ToListAsync(cancellationToken);

        await inbox.MarkProcessedAsync(marker, $"job-probe:{marker}", tenant?.Id, "probe", cancellationToken);

        Observations[marker] = new TenantObservation(tenant?.Id, tenant?.ConnectionString, emails);
    }
}

/// <summary>Declared tenant-less: proves a <c>[SystemJob]</c> runs with no tenant in scope.</summary>
[SystemJob]
[AutomaticRetry(Attempts = 0)]
public sealed class SystemProbeJob(IMultiTenantContextAccessor<AppTenantInfo> accessor)
{
    public static readonly ConcurrentDictionary<Guid, string?> Observations = new();

    public Task RunAsync(Guid marker, CancellationToken cancellationToken)
    {
        Observations[marker] = accessor.MultiTenantContext.TenantInfo?.Id;
        return Task.CompletedTask;
    }
}

/// <summary>Integration event used only to drive a handler into a tenant.</summary>
public sealed record TenantProbeIntegrationEvent(
    Guid Id,
    DateTime OccurredOnUtc,
    string? TenantId,
    string CorrelationId,
    string Source,
    Guid Marker) : IIntegrationEvent;

/// <summary>
/// The event-side twin of <see cref="TenantProbeJob"/>. Registered by
/// <c>AppWebApplicationFactory</c> so it runs through the real bus, inbox and tenant scope.
/// </summary>
public sealed class TenantProbeEventHandler(
    IMultiTenantContextAccessor<AppTenantInfo> accessor,
    IdentityDbContext identity,
    IInboxStore inbox) : IIntegrationEventHandler<TenantProbeIntegrationEvent>
{
    public static readonly ConcurrentDictionary<Guid, TenantObservation> Observations = new();

    public async Task HandleAsync(TenantProbeIntegrationEvent @event, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(@event);

        var tenant = accessor.MultiTenantContext.TenantInfo;

        var emails = await identity.Users
            .Select(u => u.Email!)
            .ToListAsync(ct);

        await inbox.MarkProcessedAsync(@event.Marker, $"event-probe:{@event.Marker}", tenant?.Id, "probe", ct);

        Observations[@event.Marker] = new TenantObservation(tenant?.Id, tenant?.ConnectionString, emails);
    }
}
