using Boilerplate.Modules.Identity.Contracts.DTOs;
using Mediator;

namespace Boilerplate.Modules.Identity.Contracts.v1.Sessions.GetMySessions;

public sealed record GetMySessionsQuery : IQuery<List<UserSessionDto>>;