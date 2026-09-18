using Boilerplate.Modules.Identity.Contracts.DTOs;
using Boilerplate.Modules.Identity.Contracts.Services;
using Boilerplate.Modules.Identity.Contracts.v1.Users.GetUserProfile;
using Mediator;

namespace Boilerplate.Modules.Identity.Features.v1.Users.GetUserProfile;

public sealed class GetCurrentUserProfileQueryHandler : IQueryHandler<GetCurrentUserProfileQuery, UserDto>
{
    private readonly IUserService _userService;

    public GetCurrentUserProfileQueryHandler(IUserService userService)
    {
        _userService = userService;
    }

    public async ValueTask<UserDto> Handle(GetCurrentUserProfileQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        return await _userService.GetAsync(query.UserId, cancellationToken).ConfigureAwait(false);
    }
}