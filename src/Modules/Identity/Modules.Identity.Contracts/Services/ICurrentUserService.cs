using Boilerplate.BuildingBlocks.Core.Context;
using System.Security.Claims;

namespace Boilerplate.Modules.Identity.Contracts.Services;

/// <summary>
/// Service interface for managing the current user context.
/// Combines user identity access with initialization capabilities.
/// </summary>
public interface ICurrentUserService : ICurrentUser, ICurrentUserInitializer
{
}