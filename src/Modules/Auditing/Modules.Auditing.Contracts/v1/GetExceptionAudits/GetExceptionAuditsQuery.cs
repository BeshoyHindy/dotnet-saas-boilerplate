using Boilerplate.Modules.Auditing.Contracts;
using Boilerplate.Modules.Auditing.Contracts.Dtos;
using Mediator;

namespace Boilerplate.Modules.Auditing.Contracts.v1.GetExceptionAudits;

public sealed class GetExceptionAuditsQuery : IQuery<IReadOnlyList<AuditSummaryDto>>
{
    public ExceptionArea? Area { get; init; }

    public AuditSeverity? Severity { get; init; }

    public string? ExceptionType { get; init; }

    public string? RouteOrLocation { get; init; }

    public DateTime? FromUtc { get; init; }

    public DateTime? ToUtc { get; init; }

    public int? Skip { get; init; }

    public int? Take { get; init; }
}