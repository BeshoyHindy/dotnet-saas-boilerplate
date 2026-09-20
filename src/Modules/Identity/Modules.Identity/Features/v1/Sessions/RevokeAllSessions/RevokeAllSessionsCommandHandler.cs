using Boilerplate.BuildingBlocks.Core.Context;
using Boilerplate.Modules.Identity.Contracts.Services;
using Boilerplate.Modules.Identity.Contracts.v1.Sessions.RevokeAllSessions;
using Mediator;

namespace Boilerplate.Modules.Identity.Features.v1.Sessions.RevokeAllSessions;

public sealed class RevokeAllSessionsCommandHandler : ICommandHandler<RevokeAllSessionsCommand, int>
{
    private readonly ISessionService _sessionService;
    private readonly ICurrentUser _currentUser;

    public RevokeAllSessionsCommandHandler(ISessionService sessionService, ICurrentUser currentUser)
    {
        _sessionService = sessionService;
        _currentUser = currentUser;
    }

    public async ValueTask<int> Handle(RevokeAllSessionsCommand command, CancellationToken cancellationToken)
    {
        var userId = _currentUser.GetUserId().ToString();
        return await _sessionService.RevokeAllSessionsAsync(
            userId,
            userId,
            command.ExceptSessionId,
            "User requested logout from all devices",
            cancellationToken);
    }
}