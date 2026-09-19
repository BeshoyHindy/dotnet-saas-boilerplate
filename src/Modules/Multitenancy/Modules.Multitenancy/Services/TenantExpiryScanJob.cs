using Boilerplate.BuildingBlocks.Eventing.Abstractions;
using Boilerplate.BuildingBlocks.Jobs;
using Boilerplate.BuildingBlocks.Shared.Multitenancy;
using Boilerplate.Modules.Multitenancy.Contracts.Events;
using Boilerplate.Modules.Multitenancy.Data;
using Boilerplate.Modules.Multitenancy.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Boilerplate.Modules.Multitenancy.Services;

/// <summary>
/// Daily scan that notifies tenants approaching or past their <c>ValidUpto</c>. For each active,
/// non-root tenant it classifies the state (nearing expiry / in grace / expired), records a dedup row
/// in <see cref="TenantExpiryNotice"/> (one per tenant+state+validity period), and publishes the
/// matching integration event. Notification side-effects (email) are handled by event consumers.
/// </summary>
/// <remarks>
/// <see cref="SystemJobAttribute"/>: the scan itself belongs to no tenant — it is the fan-out. Each
/// tenant is entered explicitly through <see cref="ITenantScope"/> before its notice is published,
/// so the outbox row lands in that tenant's database rather than the scheduler's default one.
/// </remarks>
[SystemJob]
public sealed class TenantExpiryScanJob
{
    private readonly ITenantScope _tenantScope;
    private readonly TenantDbContext _db;
    private readonly TimeProvider _timeProvider;
    private readonly TenantValidityOptions _options;
    private readonly ILogger<TenantExpiryScanJob> _logger;

    public TenantExpiryScanJob(
        ITenantScope tenantScope,
        TenantDbContext db,
        TimeProvider timeProvider,
        IOptions<TenantValidityOptions> options,
        ILogger<TenantExpiryScanJob> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _tenantScope = tenantScope;
        _db = db;
        _timeProvider = timeProvider;
        _options = options.Value;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var published = 0;

        await _tenantScope.RunForEachTenantAsync(
            async (tenant, services, ct) =>
            {
                if (!tenant.IsActive ||
                    string.Equals(tenant.Id, MultitenancyConstants.Root.Id, StringComparison.Ordinal))
                {
                    return;
                }

                try
                {
                    if (await TryNotifyAsync(tenant, services, now, ct).ConfigureAwait(false))
                    {
                        published++;
                    }
                }
#pragma warning disable CA1031 // One tenant's failure must not block the rest of the scan
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    _logger.LogError(ex, "[Multitenancy] expiry scan failed for tenant {TenantId}", tenant.Id);
                }
            },
            cancellationToken).ConfigureAwait(false);

        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("[Multitenancy] expiry scan published {Count} notice(s)", published);
        }
    }

    private async Task<bool> TryNotifyAsync(AppTenantInfo tenant, IServiceProvider tenantServices, DateTime now, CancellationToken ct)
    {
        var validUpto = tenant.ValidUpto;
        var graceEnds = validUpto.AddDays(_options.GracePeriodDays);

        string noticeType;
        if (now > graceEnds)
        {
            noticeType = TenantExpiryNoticeTypes.Expired;
        }
        else if (now > validUpto)
        {
            noticeType = TenantExpiryNoticeTypes.EnteredGrace;
        }
        else if (now >= validUpto.AddDays(-_options.ExpiryNotificationLeadDays))
        {
            noticeType = TenantExpiryNoticeTypes.NearingExpiry;
        }
        else
        {
            return false; // healthy and outside the reminder window
        }

        // Dedup: one notice per tenant per state per validity period (re-arms when ValidUpto changes).
        var alreadyNotified = await _db.TenantExpiryNotices
            .AnyAsync(x => x.TenantId == tenant.Id && x.NoticeType == noticeType && x.ValidUptoUtc == validUpto, ct)
            .ConfigureAwait(false);
        if (alreadyNotified)
        {
            return false;
        }

        _db.TenantExpiryNotices.Add(TenantExpiryNotice.Record(tenant.Id, noticeType, validUpto, now));
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        // The outbox writer comes from the tenant's own scope: EventingDbContext captures the
        // tenant's connection string at construction, so a writer resolved outside it would file the
        // row in the scheduler's database, where this tenant's dispatcher never looks.
        var outbox = tenantServices.GetRequiredService<IOutboxWriter>();
        await outbox.AddAsync(BuildEvent(noticeType, tenant, validUpto, graceEnds, now), ct).ConfigureAwait(false);
        return true;
    }

    private static IIntegrationEvent BuildEvent(
        string noticeType, AppTenantInfo tenant, DateTime validUpto, DateTime graceEnds, DateTime now)
    {
        var id = Guid.NewGuid();
        var correlationId = Guid.NewGuid().ToString();
        const string source = "Multitenancy";
        var name = tenant.Name ?? tenant.Id;
        var email = tenant.AdminEmail;

        return noticeType switch
        {
            TenantExpiryNoticeTypes.NearingExpiry => new TenantNearingExpiryIntegrationEvent(
                id, now, tenant.Id, correlationId, source, name, email, validUpto, graceEnds,
                DaysRemaining: Math.Max(0, (int)Math.Ceiling((validUpto - now).TotalDays))),
            TenantExpiryNoticeTypes.EnteredGrace => new TenantEnteredGraceIntegrationEvent(
                id, now, tenant.Id, correlationId, source, name, email, validUpto, graceEnds),
            _ => new TenantExpiredIntegrationEvent(
                id, now, tenant.Id, correlationId, source, name, email, validUpto, graceEnds),
        };
    }
}
