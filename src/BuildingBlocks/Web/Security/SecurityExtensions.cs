using Microsoft.AspNetCore.Builder;

namespace Boilerplate.BuildingBlocks.Web.Security;

public static class SecurityExtensions
{
    public static IApplicationBuilder UseHeroSecurityHeaders(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<SecurityHeadersMiddleware>();
    }
}