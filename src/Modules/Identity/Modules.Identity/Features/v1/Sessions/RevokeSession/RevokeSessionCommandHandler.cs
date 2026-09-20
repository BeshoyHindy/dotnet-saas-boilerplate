using Boilerplate.BuildingBlocks.Core.Context;
using Boilerplate.Modules.Identity.Contracts.Services;
using Boilerplate.Modules.Identity.Contracts.v1.Sessions.RevokeSession;
using Mediator;

namespace Boilerplate.Modules.Identity.Features.v1.Sessions.RevokeSession;

public sealed class RevokeSessionCommandHandler : ICommandHandler<RevokeSessionCommand, bool>
{
    private readonly ISessionService _sessionService;
    private readonly ICurrentUser _currentUser;

    public RevokeSessionCommandHandler(ISessionService sessionService, ICurrentUser currentUser)
    {
        _sessionService = sessionService;
        _currentUser = currentUser;
    }

    public async ValueTask<bool> Handle(RevokeSessionCommand command, CancellationToken cancellationToken)
    {
        var userId = _currentUser.GetUserId().ToString();
        return await _sessionService.RevokeSessionAsync(
            command.SessionId,
            userId,
            "User requested",
            cancellationToken);
    }
}