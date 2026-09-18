using Boilerplate.Modules.Auditing.Contracts.Dtos;
using Mediator;

namespace Boilerplate.Modules.Auditing.Contracts.v1.GetAuditSummary;

public sealed class GetAuditSummaryQuery : IQuery<AuditSummaryAggregateDto>
{
    public DateTime? FromUtc { get; init; }

    public DateTime? ToUtc { get; init; }

    public string? TenantId { get; init; }
}