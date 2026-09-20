using Boilerplate.Modules.Identity.Contracts.DTOs;
using Mediator;

namespace Boilerplate.Modules.Identity.Contracts.v1.Users.GetUserProfile;

public sealed record GetCurrentUserProfileQuery(string UserId) : IQuery<UserDto>;