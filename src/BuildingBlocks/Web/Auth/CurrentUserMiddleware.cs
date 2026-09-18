using Boilerplate.BuildingBlocks.Core.Context;
using Boilerplate.BuildingBlocks.Shared.Identity.Claims;
using Microsoft.AspNetCore.Http;
using System.Diagnostics;

namespace Boilerplate.BuildingBlocks.Web.Auth;

public class CurrentUserMiddleware(ICurrentUserInitializer currentUserInitializer) : IMiddleware
{
    private readonly ICurrentUserInitializer _currentUserInitializer = currentUserInitializer;

    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        _currentUserInitializer.SetCurrentUser(context.User);

        var activity = Activity.Current;
        if (activity is not null && context.User?.Identity?.IsAuthenticated == true)
        {
            var userId = context.User.GetUserId();
            var tenant = context.User.GetTenant();
            var correlationId = context.Request.HttpContext.TraceIdentifier;

            if (!string.IsNullOrEmpty(userId))
                activity.SetTag("boilerplate.user_id", userId);

            if (!string.IsNullOrEmpty(tenant))
                activity.SetTag("boilerplate.tenant_id", tenant);

            if (!string.IsNullOrEmpty(correlationId))
                activity.SetTag("boilerplate.correlation_id", correlationId);
        }

        await next(context);
    }
}