using Boilerplate.BuildingBlocks.Shared.Multitenancy;
using Boilerplate.Modules.Auditing.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Boilerplate.Modules.Auditing.Persistence;

/// <summary>
/// Persists audit envelopes into SQL using EF Core.
/// </summary>
public sealed class SqlAuditSink : IAuditSink
{
    private readonly ITenantScope _tenantScope;
    private readonly IAuditSerializer _serializer;
    private readonly ILogger<SqlAuditSink> _log;

    public SqlAuditSink(ITenantScope tenantScope, IAuditSerializer serializer, ILogger<SqlAuditSink> log)
        => (_tenantScope, _serializer, _log) = (tenantScope, serializer, log);

    public async Task WriteAsync(IReadOnlyList<AuditEnvelope> batch, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (batch.Count == 0) return;

        // One tenant scope per group: the AuditDbContext has to be built under the tenant whose rows
        // it is about — for the tenant filter, and for a dedicated connection string. Envelopes with
        // no tenant are platform events and are filed against the root tenant, as before.
        foreach (var group in batch.GroupBy(e => e.TenantId))
        {
            var tenantId = group.Key ?? MultitenancyConstants.Root.Id;
            var records = group.Select(ToRecord).ToList();

            try
            {
                await _tenantScope.RunAsync(
                    tenantId,
                    async (services, token) =>
                    {
                        var db = services.GetRequiredService<AuditDbContext>();
                        db.AuditRecords.AddRange(records);
                        await db.SaveChangesAsync(token).ConfigureAwait(false);
                    },
                    ct).ConfigureAwait(false);
            }
            catch (UnknownTenantException ex)
            {
                // A tenant deleted between the event and the flush. Losing its audit rows beats
                // failing the whole batch, which would lose everyone else's too.
                _log.LogWarning(ex, "Skipping audit write for tenant {TenantId} because tenant was not found.", tenantId);
                continue;
            }

            if (_log.IsEnabled(LogLevel.Information))
            {
                _log.LogInformation("Wrote {Count} audit records for tenant {TenantId}.", records.Count, tenantId);
            }
        }
    }

    private AuditRecord ToRecord(AuditEnvelope e) => new()
    {
        Id = e.Id,
        OccurredAtUtc = e.OccurredAtUtc,
        ReceivedAtUtc = e.ReceivedAtUtc,
        EventType = (int)e.EventType,
        Severity = (byte)e.Severity,
        TenantId = e.TenantId,
        UserId = e.UserId,
        UserName = e.UserName,
        TraceId = e.TraceId,
        SpanId = e.SpanId,
        CorrelationId = e.CorrelationId,
        RequestId = e.RequestId,
        Source = e.Source,
        Tags = (long)e.Tags,
        PayloadJson = _serializer.SerializePayload(e.Payload)
    };
}
