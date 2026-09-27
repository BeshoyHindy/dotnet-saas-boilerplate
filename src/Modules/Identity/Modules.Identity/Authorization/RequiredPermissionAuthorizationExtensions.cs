using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Boilerplate.Modules.Identity.Authorization;

public static class RequiredPermissionDefaults
{
    public const string PolicyName = "RequiredPermission";
}

public static class RequiredPermissionAuthorizationExtensions
{
    public static AuthorizationPolicyBuilder RequireRequiredPermissions(this AuthorizationPolicyBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.AddRequirements(new PermissionAuthorizationRequirement());
    }

    public static AuthorizationBuilder AddRequiredPermissionPolicy(this AuthorizationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.AddPolicy(RequiredPermissionDefaults.PolicyName, policy =>
        {
            policy.RequireAuthenticatedUser();
            // The forwarding default scheme, not "Bearer" directly: it is bearer for every request
            // except a Job monitor request carrying the Job monitor cookie (ADR-0009). Naming Bearer
            // here would re-authenticate that request with a scheme that ignores the cookie.
            policy.AddAuthenticationSchemes(Jwt.JobMonitorCookieAuthentication.SelectorScheme);
            policy.RequireRequiredPermissions();
        });

        builder.Services.TryAddEnumerable(ServiceDescriptor.Scoped<IAuthorizationHandler, RequiredPermissionAuthorizationHandler>());

        return builder;
    }
}