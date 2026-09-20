using Boilerplate.Modules.Auditing.Contracts;
using Boilerplate.Modules.Auditing.Contracts.Dtos;
using Mediator;

namespace Boilerplate.Modules.Auditing.Contracts.v1.GetSecurityAudits;

public sealed class GetSecurityAuditsQuery : IQuery<IReadOnlyList<AuditSummaryDto>>
{
    public SecurityAction? Action { get; init; }

    public string? UserId { get; init; }

    public string? TenantId { get; init; }

    public DateTime? FromUtc { get; init; }

    public DateTime? ToUtc { get; init; }

    public int? Skip { get; init; }

    public int? Take { get; init; }
}