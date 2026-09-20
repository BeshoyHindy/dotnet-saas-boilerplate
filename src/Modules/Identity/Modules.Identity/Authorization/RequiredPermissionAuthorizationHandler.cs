using Boilerplate.BuildingBlocks.Shared.Identity.Authorization;
using Boilerplate.BuildingBlocks.Shared.Identity.Claims;
using Boilerplate.Modules.Identity.Contracts.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;

namespace Boilerplate.Modules.Identity.Authorization;

public sealed class RequiredPermissionAuthorizationHandler(IUserService userService) : AuthorizationHandler<PermissionAuthorizationRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionAuthorizationRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requirement);

        var httpContext = context.Resource as HttpContext;
        var endpoint = context.Resource switch
        {
            HttpContext ctx => ctx.GetEndpoint(),
            Endpoint ep => ep,
            _ => null,
        };

        // Fail closed: no endpoint means no declared intent (unmapped path, non-HTTP resource), so
        // there is nothing to authorize against. Never succeed by default.
        if (endpoint is null)
        {
            return;
        }

        // IMPORTANT: resolve IRequiredPermissionMetadata from Boilerplate.BuildingBlocks.Shared.Identity.Authorization (the
        // interface the attribute implements) — a duplicate would silently fail-open every .RequirePermission().
        var permissionMetadata = endpoint.Metadata.GetMetadata<IRequiredPermissionMetadata>();
        if (permissionMetadata is not null)
        {
            var requiredPermissions = permissionMetadata.RequiredPermissions;

            // A declared-but-empty set states no requirement the caller can satisfy; treating it as
            // satisfied would turn a typo (.RequirePermission(null)) into an open endpoint.
            if (requiredPermissions is not { Count: > 0 } || context.User?.GetUserId() is not { } userId)
            {
                return;
            }

            // All-of: the caller must hold every listed permission. Sequential with a short circuit —
            // IUserPermissionService answers each call from one cached permission set per user, so this
            // is one load and N set lookups, not N round-trips.
            var cancellationToken = httpContext?.RequestAborted ?? CancellationToken.None;
            foreach (string permission in requiredPermissions)
            {
                if (!await userService.HasPermissionAsync(userId, permission, cancellationToken).ConfigureAwait(false))
                {
                    return;
                }
            }

            context.Succeed(requirement);
            return;
        }

        // No permission metadata: the endpoint may still declare that authentication alone is the gate.
        // The policy itself already required an authenticated user, so the marker is the whole check.
        // Anything else (AllowAnonymous is handled upstream by the authorization middleware) is denied.
        if (endpoint.Metadata.GetMetadata<IAuthenticatedOnlyMetadata>() is not null)
        {
            context.Succeed(requirement);
        }
    }
}