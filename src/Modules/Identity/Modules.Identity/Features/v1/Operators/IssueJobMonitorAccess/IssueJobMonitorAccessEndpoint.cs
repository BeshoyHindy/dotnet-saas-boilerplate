using Boilerplate.BuildingBlocks.Jobs;
using Boilerplate.BuildingBlocks.Shared.Constants;
using Boilerplate.BuildingBlocks.Shared.Identity.Authorization;
using Boilerplate.Modules.Identity.Contracts.v1.Operators.IssueJobMonitorAccess;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;

namespace Boilerplate.Modules.Identity.Features.v1.Operators.IssueJobMonitorAccess;

public static class IssueJobMonitorAccessEndpoint
{
    internal static RouteHandlerBuilder MapIssueJobMonitorAccessEndpoint(this IEndpointRouteBuilder endpoints)
    {
        return endpoints.MapPost("/operator/job-monitor-access",
            async Task<Results<Ok<JobMonitorAccessResponse>, ProblemHttpResult>>
            (HttpContext httpContext,
             [FromServices] IMediator mediator,
             [FromServices] IConfiguration configuration,
             CancellationToken ct) =>
            {
                var grant = await mediator.Send(new IssueJobMonitorAccessCommand(), ct);

                // Cookie path and mapped route come from the same place, or the browser never sends it.
                var path = JobMonitor.RouteFrom(configuration);
                JobMonitorCookie.Append(httpContext, path, grant.Token, grant.ExpiresAtUtc);

                return TypedResults.Ok(new JobMonitorAccessResponse(path, grant.ExpiresAtUtc));
            })
            .WithName("IssueJobMonitorAccess")
            .WithSummary("Open the Job monitor from a browser")
            .WithDescription(
                "ADR-0009. Sets a short-lived cookie that opens the Job monitor (the Hangfire dashboard) " +
                "in this browser: HttpOnly, Secure, SameSite=Strict, host-only, Path set to the Job monitor " +
                "route, a fixed 15-minute life, bound to the caller's session — logging out or revoking the " +
                "session ends it. The cookie is accepted on the Job monitor route only. Root operators only; " +
                "refused while acting. Every issue is recorded as a security audit event. The body names the " +
                "path to open and when the cookie expires; it never contains the credential.")
            // Root-only permission (IsRoot in the catalog), plus a root-tenant check in the handler.
            .RequirePermission(SystemPermissions.Hangfire.View)
            .DenyWhenActing()
            .Produces<JobMonitorAccessResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);
    }
}
