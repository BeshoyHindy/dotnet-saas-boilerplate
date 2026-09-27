using Boilerplate.BuildingBlocks.Jobs;
using Boilerplate.BuildingBlocks.Shared.Constants;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using System.Security.Claims;
using System.Text;

namespace Boilerplate.Modules.Identity.Authorization.Jwt;

/// <summary>
/// The second authentication scheme (ADR-0009): a signed token delivered as
/// <see cref="JobMonitorCookie"/> and accepted on the Job monitor route only. Everything else in the
/// API stays bearer-only.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a forwarding default scheme, not per-endpoint schemes.</b> Tenant resolution and the
/// token-without-tenant guard read <see cref="HttpContext.User"/> in module middleware, which runs
/// after <c>UseAuthentication</c> but before <c>UseAuthorization</c>. Schemes named on an endpoint
/// are only authenticated by the authorization middleware — too late: the request would reach the
/// permission check with no tenant. So <see cref="SelectorScheme"/> is the default authenticate
/// scheme and picks the cookie scheme only when <see cref="Select"/> says so; every other request
/// is authenticated by the bearer scheme exactly as before.
/// </para>
/// <para>
/// <b>Why neither credential works as the other.</b> The cookie's token is minted for
/// <see cref="AudienceFor"/>, which the bearer scheme does not accept; this scheme accepts nothing
/// else. A Job monitor token sent as a bearer header is 401 everywhere, and an access token placed in
/// the cookie is 401 on the Job monitor.
/// </para>
/// </remarks>
internal static class JobMonitorCookieAuthentication
{
    /// <summary>The default authenticate scheme: forwards to bearer, or to the cookie on the Job monitor.</summary>
    public const string SelectorScheme = "BearerOrJobMonitorCookie";

    /// <summary>The audience a Job monitor token is minted for and the only one this scheme accepts.</summary>
    public static string AudienceFor(JwtOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return $"{options.Audience}/job-monitor";
    }

    /// <summary>
    /// The cookie scheme answers only when the matched endpoint is the Job monitor, the request
    /// carries no bearer header (a bearer header always wins, so API clients are unaffected), and the
    /// cookie is present. Anything else goes to the bearer scheme.
    /// </summary>
    public static string Select(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var isJobMonitor = context.GetEndpoint()?.Metadata.GetMetadata<JobMonitorEndpointMetadata>() is not null;

        return isJobMonitor
            && string.IsNullOrEmpty(context.Request.Headers.Authorization)
            && JobMonitorCookie.Read(context) is not null
                ? JobMonitor.CookieScheme
                : JwtBearerDefaults.AuthenticationScheme;
    }
}

/// <summary>
/// Configures the <see cref="JobMonitor.CookieScheme"/> instance of the JWT handler: same key,
/// issuer and algorithm as the bearer scheme, its own audience, the token read from the cookie
/// only, and the session behind it checked on every request.
/// </summary>
internal sealed class ConfigureJobMonitorCookieOptions(IOptions<JwtOptions> jwtOptions)
    : IConfigureNamedOptions<JwtBearerOptions>
{
    private readonly JwtOptions _options = jwtOptions.Value;

    public void Configure(JwtBearerOptions options) => Configure(Options.DefaultName, options);

    public void Configure(string? name, JwtBearerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (name != JobMonitor.CookieScheme)
        {
            return;
        }

        options.RequireHttpsMetadata = true;
        options.SaveToken = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.ASCII.GetBytes(_options.SigningKey)),
            ValidIssuer = _options.Issuer,
            ValidateIssuer = true,
            ValidateLifetime = true,
            ValidAudience = JobMonitorCookieAuthentication.AudienceFor(_options),
            ValidateAudience = true,
            RoleClaimType = ClaimTypes.Role,
            ClockSkew = TimeSpan.Zero,
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
        };

        options.Events = new JwtBearerEvents
        {
            // The cookie, and only the cookie. The selector never routes a request with a bearer
            // header here, and this scheme must not accept one if something else ever did.
            OnMessageReceived = context =>
            {
                context.Token = JobMonitorCookie.Read(context.HttpContext);
                if (context.Token is null)
                {
                    context.NoResult();
                }

                return Task.CompletedTask;
            },

            // Left for the bearer challenge, which surfaces it in Development like its own failures.
            OnAuthenticationFailed = context =>
            {
                context.HttpContext.Items[ConfigureJwtBearerOptions.AuthFailureItemKey] =
                    $"Job monitor cookie: {context.Exception.GetType().Name}: {context.Exception.Message}";
                return Task.CompletedTask;
            },

            // Logging out or revoking the session cuts Job monitor access on the next request: the
            // same sid check the bearer scheme runs. An acting identity never gets here — the issuing
            // endpoint refuses acting tokens — and is refused if it somehow does.
            OnTokenValidated = async context =>
            {
                if (context.Principal?.HasClaim(c => c.Type == ClaimConstants.ActorSubject) == true)
                {
                    ConfigureJwtBearerOptions.Reject(
                        context, ConfigureJwtBearerOptions.AuthFailureItemKey, "Job monitor token carries an actor");
                    return;
                }

                await ConfigureJwtBearerOptions
                    .RejectUnlessSessionIsLiveAsync(context, ConfigureJwtBearerOptions.AuthFailureItemKey)
                    .ConfigureAwait(false);
            },
        };
    }
}
