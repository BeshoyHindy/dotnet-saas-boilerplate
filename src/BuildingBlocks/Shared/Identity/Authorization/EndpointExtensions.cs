using Microsoft.AspNetCore.Builder;

namespace Boilerplate.BuildingBlocks.Shared.Identity.Authorization;

public static class EndpointExtensions
{
    public static TBuilder RequirePermission<TBuilder>(
    this TBuilder endpointConventionBuilder, string requiredPermission, params string[] additionalRequiredPermissions)
    where TBuilder : IEndpointConventionBuilder
    {
        return endpointConventionBuilder.WithMetadata(new RequiredPermissionAttribute(requiredPermission, additionalRequiredPermissions));
    }

    /// <summary>
    /// Declares "any signed-in caller may use this" for self-service endpoints that scope themselves
    /// to the caller. The permission policy denies endpoints that declare nothing, so this marker is
    /// how a route says it needs authentication and nothing more.
    /// </summary>
    public static TBuilder RequireAuthenticatedOnly<TBuilder>(this TBuilder endpointConventionBuilder)
    where TBuilder : IEndpointConventionBuilder
    {
        return endpointConventionBuilder.WithMetadata(new AuthenticatedOnlyAttribute());
    }
}