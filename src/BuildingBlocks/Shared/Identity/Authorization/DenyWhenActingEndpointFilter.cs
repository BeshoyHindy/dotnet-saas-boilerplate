using Boilerplate.BuildingBlocks.Core.Exceptions;
using Boilerplate.BuildingBlocks.Shared.Constants;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Boilerplate.BuildingBlocks.Shared.Identity.Authorization;

/// <summary>
/// Endpoint filter that blocks a request when the caller's token carries
/// <see cref="ClaimConstants.ActorSubject"/> — i.e. someone is acting on behalf of the token's
/// subject (impersonation or an exchanged operator token). Apply it to every endpoint that
/// changes or reveals the subject's own credentials (2FA enrollment/disable, change password,
/// recovery codes, ...), so an actor can never use a "sign in as" session to take over the
/// subject's account.
/// </summary>
public sealed class DenyWhenActingEndpointFilter : IEndpointFilter
{
    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        if (context.HttpContext.User.HasClaim(c => c.Type == ClaimConstants.ActorSubject))
        {
            throw new ForbiddenException("not available while acting on behalf of another user");
        }

        return next(context);
    }
}

public static class DenyWhenActingEndpointExtensions
{
    /// <summary>
    /// Rejects the request with 403 when the caller's token carries an actor subject (impersonation
    /// or operator token exchange). Use on self-service endpoints that would otherwise let an actor
    /// change or reveal the credentials of the user they are acting as.
    /// </summary>
    public static RouteHandlerBuilder DenyWhenActing(this RouteHandlerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddEndpointFilter<DenyWhenActingEndpointFilter>();
    }
}
