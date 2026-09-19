using Boilerplate.BuildingBlocks.Shared.Multitenancy;
using Boilerplate.Modules.Identity.Contracts.DTOs;
using Boilerplate.Modules.Identity.Contracts.v1.Tokens.TokenGeneration;
using Finbuckle.MultiTenant.Abstractions;
using Mediator;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Boilerplate.Modules.Identity.Features.v1.Tokens.TokenGeneration;

public static class GenerateTokenEndpoint
{
    /// <summary>
    /// Header used by clients to identify which app shell is requesting the token.
    /// SuperAdmin (root tenant) accounts are restricted to the admin app — submitting
    /// "dashboard" with tenant=root yields a 403 instead of a useful token. This is a
    /// belt-and-braces check; the dashboard client also rejects root-tenant tokens
    /// locally for a cleaner UX.
    /// </summary>
    public const string AppHeader = "X-Client-App";
    public const string AppAdmin = "admin";
    public const string AppDashboard = "dashboard";

    public static RouteHandlerBuilder MapGenerateTokenEndpoint(this IEndpointRouteBuilder endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        return endpoint.MapPost("/token",
            [AllowAnonymous] async Task<Results<Ok<TokenResponse>, UnauthorizedHttpResult, ProblemHttpResult>>
            ([FromBody] GenerateTokenCommand command,
            [FromHeader(Name = AppHeader)] string? app,
            [FromServices] IMultiTenantContextAccessor<AppTenantInfo> tenantAccessor,
            [FromServices] IMediator mediator,
            CancellationToken ct) =>
            {
                // The tenant comes from the {tenant} route segment via tenant resolution — never from a
                // caller-supplied header. An unknown tenant resolves to null and the command fails 401,
                // which is exactly what a wrong password returns.
                var tenant = tenantAccessor.MultiTenantContext?.TenantInfo?.Id;

                if (IsRootViaDashboard(tenant, app))
                {
                    return TypedResults.Problem(
                        statusCode: StatusCodes.Status403Forbidden,
                        title: "App boundary",
                        detail: "SuperAdmin accounts must use the admin app. Sign in there instead of the tenant dashboard.");
                }

                var token = await mediator.Send(command, ct);
                return token is null
                    ? TypedResults.Unauthorized()
                    : TypedResults.Ok(token);
            })
            .WithName("IssueJwtTokens")
            .WithSummary("Issue JWT access and refresh tokens")
            .WithDescription("Submit credentials to receive a JWT access token and a refresh token. The tenant is taken from the '{tenant}' route segment. The 'X-Client-App' header (admin|dashboard) is used to enforce the SuperAdmin / dashboard boundary.")
            .Produces<TokenResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status500InternalServerError);
    }

    private static bool IsRootViaDashboard(string? tenant, string? app)
    {
        return string.Equals(tenant, MultitenancyConstants.Root.Id, StringComparison.OrdinalIgnoreCase)
            && string.Equals(app, AppDashboard, StringComparison.OrdinalIgnoreCase);
    }
}
