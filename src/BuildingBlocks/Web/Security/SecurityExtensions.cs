using Microsoft.AspNetCore.Builder;

namespace Boilerplate.BuildingBlocks.Web.Security;

public static class SecurityExtensions
{
    public static IApplicationBuilder UseAppSecurityHeaders(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<SecurityHeadersMiddleware>();
    }
}