using Boilerplate.BuildingBlocks.Core.Exceptions;
using Boilerplate.BuildingBlocks.Shared.Constants;
using Boilerplate.BuildingBlocks.Shared.Identity.Claims;
using Boilerplate.Modules.Identity.Contracts.Services;
using Boilerplate.Modules.Identity.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;

namespace Boilerplate.Modules.Identity.Authorization.Jwt;

public class ConfigureJwtBearerOptions : IConfigureNamedOptions<JwtBearerOptions>
{
    private readonly JwtOptions _options;
    private readonly IHostEnvironment _environment;

    public ConfigureJwtBearerOptions(IOptions<JwtOptions> options, IConfiguration configuration, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        _options = options.Value;
        _environment = environment;
    }

    public void Configure(JwtBearerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        Configure(string.Empty, options);
    }

    public void Configure(string? name, JwtBearerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (name != JwtBearerDefaults.AuthenticationScheme)
        {
            return;
        }

        byte[] key = Encoding.ASCII.GetBytes(_options.SigningKey);

        options.RequireHttpsMetadata = true;
        options.SaveToken = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(key),
            ValidIssuer = _options.Issuer,
            ValidateIssuer = true,
            ValidateLifetime = true,
            ValidAudience = _options.Audience,
            ValidateAudience = true,
            RoleClaimType = ClaimTypes.Role,
            ClockSkew = TimeSpan.FromMinutes(2),
            // V9.1.2: pin the allow-listed algorithm to what TokenService actually signs with,
            // rather than trusting whatever Microsoft.IdentityModel accepts for the key type.
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256]
        };
        // Capture the validation failure reason so OnChallenge can include it (in Development).
        // Without this we get a body of `{"error":"Unauthorized"}` with no clue why JwtBearer rejected.
        const string FailureKey = "JwtAuthFailure";
        bool isDev = _environment.IsDevelopment();

        options.Events = new JwtBearerEvents
        {
            OnAuthenticationFailed = context =>
            {
                // Stash the exception type+message on HttpContext so OnChallenge can surface it.
                context.HttpContext.Items[FailureKey] =
                    $"{context.Exception.GetType().Name}: {context.Exception.Message}";

                // Server-side log so we can also see the rejection reason in the API console.
                var failedLogger = context.HttpContext.RequestServices
                    .GetRequiredService<ILoggerFactory>()
                    .CreateLogger("Boilerplate.Identity.JwtAuth");
                failedLogger.LogWarning(context.Exception,
                    "JwtBearer authentication FAILED for {Method} {Path}: {Reason}",
                    SanitizeForLog(context.HttpContext.Request.Method),
                    SanitizeForLog(context.HttpContext.Request.Path.ToString()),
                    context.Exception.Message);
                return Task.CompletedTask;
            },
            OnChallenge = context =>
            {
                context.HandleResponse();
                if (!context.Response.HasStarted)
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    context.Response.ContentType = "application/problem+json";

                    // Was an Authorization header even sent? Helps distinguish "JWT rejected"
                    // from "no token at all" — both produce 401 but for very different reasons.
                    bool hadAuthHeader = !string.IsNullOrEmpty(context.HttpContext.Request.Headers.Authorization);

                    // RFC 9457 ProblemDetails — matches the contract the rest of the API uses
                    // for error responses (via the global exception handler).
                    var problem = new ProblemDetails
                    {
                        Type = "https://datatracker.ietf.org/doc/html/rfc7235#section-3.1",
                        Title = "Unauthorized",
                        Status = StatusCodes.Status401Unauthorized,
                        Detail = "Authentication is required to access this resource.",
                        Instance = context.HttpContext.Request.Path,
                    };

                    // In Development surface the actual JwtBearer rejection reason; in Production keep the
                    // body opaque to avoid leaking validation internals.
                    if (isDev)
                    {
                        if (context.HttpContext.Items[FailureKey] is string reason)
                        {
                            problem.Extensions["reason"] = reason;
                        }
                        else if (!hadAuthHeader)
                        {
                            problem.Extensions["reason"] = "No Authorization header on the request.";
                        }
                        else
                        {
                            // Header present but JwtBearer didn't fire OnAuthenticationFailed —
                            // typically means the bearer scheme didn't match the AuthorizationPolicy.
                            problem.Extensions["reason"] = "Bearer token present but JwtBearer did not validate it (scheme mismatch?).";
                        }

                        var challengeLogger = context.HttpContext.RequestServices
                            .GetRequiredService<ILoggerFactory>()
                            .CreateLogger("Boilerplate.Identity.JwtAuth");
                        challengeLogger.LogWarning(
                            "JwtBearer challenge for {Method} {Path}: hadAuthHeader={HadHeader} reason={Reason}",
                            SanitizeForLog(context.HttpContext.Request.Method),
                            SanitizeForLog(context.HttpContext.Request.Path.ToString()),
                            hadAuthHeader,
                            problem.Extensions["reason"]);
                    }

                    var traceId = context.HttpContext.TraceIdentifier;
                    if (!string.IsNullOrEmpty(traceId))
                    {
                        problem.Extensions["traceId"] = traceId;
                    }

                    var result = System.Text.Json.JsonSerializer.Serialize(problem);
                    return context.Response.WriteAsync(result);
                }
                return Task.CompletedTask;
            },
            // Server-side teeth behind revocation: a token is refused on its next request once what it
            // was minted from is gone — its session (sid) for a signed-in user, its grant (jti) for an
            // acting token. Without this, revoking either would only bite when the token expired.
            OnTokenValidated = async context =>
            {
                var actSub = context.Principal?.FindFirstValue(ClaimConstants.ActorSubject);
                if (string.IsNullOrEmpty(actSub))
                {
                    // Not an acting token: it was minted at login or refresh, and its session decides.
                    await RejectUnlessSessionIsLiveAsync(context, FailureKey).ConfigureAwait(false);
                    return;
                }

                // An acting token (impersonation or tenant exchange) is access-only and has no session
                // row — ImpersonationTokenIssuer mints it without a sid. Its grant, keyed by jti, is
                // its revocation record, so the grant check below is its whole liveness check.

                var jti = context.Principal?.FindFirstValue(JwtRegisteredClaimNames.Jti);
                if (string.IsNullOrEmpty(jti))
                {
                    // jti is always minted by Start; reject defensively so a malformed/stripped token
                    // can't bypass the revocation check.
                    context.HttpContext.Items[FailureKey] = "Impersonation token missing jti claim";
                    context.Fail("impersonation token missing jti claim");
                    return;
                }

                // Resolve in a CHILD scope: this hook runs before Finbuckle resolves the tenant, so a request-
                // scoped IdentityDbContext would cache a null-tenant context and NRE later tenant query filters.
                await using var hookScope = context.HttpContext.RequestServices.CreateAsyncScope();
                var grants = hookScope.ServiceProvider
                    .GetRequiredService<IImpersonationGrantService>();
                var revoked = await grants
                    .IsRevokedOrEndedAsync(jti, context.HttpContext.RequestAborted)
                    .ConfigureAwait(false);

                if (revoked)
                {
                    context.HttpContext.Items[FailureKey] = "Impersonation grant revoked or ended";
                    context.Fail("impersonation grant revoked or ended");
                }
            },
            OnForbidden = _ => throw new ForbiddenException(),
        };
    }

    /// <summary>
    /// Fails authentication (→ 401) unless the session named by the token's <c>sid</c> is live in the
    /// token's tenant. See <see cref="SessionLiveness"/> for the cache and the cross-instance bound.
    /// </summary>
    /// <remarks>
    /// A token with no <c>sid</c> — or one that is not a session id — is refused outright, not waved
    /// through. Login and refresh always mint one, and the only issuer that legitimately omits it (the
    /// acting-token issuer) is handled by the grant branch before this runs. Anything else without a
    /// <c>sid</c> names no session, so nothing could ever revoke it.
    /// </remarks>
    private static async Task RejectUnlessSessionIsLiveAsync(TokenValidatedContext context, string failureKey)
    {
        var principal = context.Principal;

        // The JWT handler's default inbound map rewrites `sid` to ClaimTypes.Sid; read both spellings.
        var sid = principal?.FindFirstValue(JwtRegisteredClaimNames.Sid)
            ?? principal?.FindFirstValue(ClaimTypes.Sid);
        if (!Guid.TryParse(sid, out var sessionId))
        {
            Reject(context, failureKey, "Access token names no session (sid)");
            return;
        }

        var tenantId = principal?.GetTenant();
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            // The tenant guard would refuse this request too; without a tenant there is no session
            // table to look in.
            Reject(context, failureKey, "Access token names no tenant");
            return;
        }

        var live = await context.HttpContext.RequestServices
            .GetRequiredService<SessionLiveness>()
            .IsLiveAsync(tenantId, sessionId, context.HttpContext.RequestAborted)
            .ConfigureAwait(false);

        if (!live)
        {
            Reject(context, failureKey, "Session revoked, expired or unknown");
        }
    }

    private static void Reject(TokenValidatedContext context, string failureKey, string reason)
    {
        // Stashed for OnChallenge, which surfaces it in Development like the other failure reasons.
        context.HttpContext.Items[failureKey] = reason;
        context.Fail(reason);
    }

    // Strip control chars so attacker-controlled request data can't forge log lines
    // (CodeQL cs/log-injection); defence in depth on top of Kestrel's URI validation.
    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var buffer = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            buffer.Append(char.IsControl(c) ? '_' : c);
        }
        return buffer.ToString();
    }
}