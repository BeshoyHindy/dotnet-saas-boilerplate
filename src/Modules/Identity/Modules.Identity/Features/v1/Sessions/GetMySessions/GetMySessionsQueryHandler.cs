using Boilerplate.BuildingBlocks.Core.Context;
using Boilerplate.Modules.Identity.Contracts.DTOs;
using Boilerplate.Modules.Identity.Contracts.Services;
using Boilerplate.Modules.Identity.Contracts.v1.Sessions.GetMySessions;
using Mediator;

namespace Boilerplate.Modules.Identity.Features.v1.Sessions.GetMySessions;

public sealed class GetMySessionsQueryHandler : IQueryHandler<GetMySessionsQuery, List<UserSessionDto>>
{
    private readonly ISessionService _sessionService;
    private readonly ICurrentUser _currentUser;

    public GetMySessionsQueryHandler(ISessionService sessionService, ICurrentUser currentUser)
    {
        _sessionService = sessionService;
        _currentUser = currentUser;
    }

    public async ValueTask<List<UserSessionDto>> Handle(GetMySessionsQuery query, CancellationToken cancellationToken)
    {
        var userId = _currentUser.GetUserId().ToString();
        return await _sessionService.GetUserSessionsAsync(userId, cancellationToken);
    }
}