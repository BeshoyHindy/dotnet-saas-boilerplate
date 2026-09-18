using Boilerplate.Modules.Billing.Contracts.Authorization;
using Boilerplate.BuildingBlocks.Shared.Identity.Authorization;
using Boilerplate.BuildingBlocks.Web.Idempotency;
using Boilerplate.Modules.Billing.Contracts.v1.Invoices;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Boilerplate.Modules.Billing.Features.v1.Invoices.MarkInvoicePaid;

public static class MarkInvoicePaidEndpoint
{
    internal static RouteHandlerBuilder MapMarkInvoicePaidEndpoint(this IEndpointRouteBuilder endpoints)
    {
        return endpoints.MapPost("/invoices/{invoiceId:guid}/pay",
                async (Guid invoiceId, IMediator mediator, CancellationToken ct) =>
                    Results.Ok(await mediator.Send(new MarkInvoicePaidCommand(invoiceId), ct)))
            .WithName("MarkInvoicePaid")
            .WithSummary("Mark an issued invoice as paid (manual, no payment processor)")
            .RequirePermission(BillingPermissions.Manage)
            .WithIdempotency();
    }
}
