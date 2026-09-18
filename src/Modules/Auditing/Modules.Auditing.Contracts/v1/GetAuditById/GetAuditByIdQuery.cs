using Boilerplate.Modules.Auditing.Contracts.Dtos;
using Mediator;

namespace Boilerplate.Modules.Auditing.Contracts.v1.GetAuditById;

public sealed record GetAuditByIdQuery(Guid Id) : IQuery<AuditDetailDto>;