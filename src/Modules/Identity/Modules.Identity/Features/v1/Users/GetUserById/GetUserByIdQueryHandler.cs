using Boilerplate.Modules.Identity.Contracts.DTOs;
using Boilerplate.Modules.Identity.Contracts.Services;
using Boilerplate.Modules.Identity.Contracts.v1.Users.GetUser;
using Mediator;

namespace Boilerplate.Modules.Identity.Features.v1.Users.GetUserById;

public sealed class GetUserByIdQueryHandler : IQueryHandler<GetUserQuery, UserDto>
{
    private readonly IUserService _userService;

    public GetUserByIdQueryHandler(IUserService userService)
    {
        _userService = userService;
    }

    public async ValueTask<UserDto> Handle(GetUserQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        return await _userService.GetAsync(query.Id, cancellationToken).ConfigureAwait(false);
    }
}