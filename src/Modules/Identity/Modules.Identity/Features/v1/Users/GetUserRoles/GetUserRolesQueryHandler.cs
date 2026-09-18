using Boilerplate.Modules.Identity.Contracts.DTOs;
using Boilerplate.Modules.Identity.Contracts.Services;
using Boilerplate.Modules.Identity.Contracts.v1.Users.GetUserRoles;
using Mediator;

namespace Boilerplate.Modules.Identity.Features.v1.Users.GetUserRoles;

public sealed class GetUserRolesQueryHandler : IQueryHandler<GetUserRolesQuery, List<UserRoleDto>>
{
    private readonly IUserService _userService;

    public GetUserRolesQueryHandler(IUserService userService)
    {
        _userService = userService;
    }

    public async ValueTask<List<UserRoleDto>> Handle(GetUserRolesQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        return await _userService.GetUserRolesAsync(query.UserId, cancellationToken).ConfigureAwait(false);
    }
}