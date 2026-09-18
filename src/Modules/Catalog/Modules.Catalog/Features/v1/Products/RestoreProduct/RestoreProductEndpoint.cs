using Boilerplate.BuildingBlocks.Shared.Identity.Authorization;
using Boilerplate.BuildingBlocks.Web.Idempotency;
using Boilerplate.Modules.Catalog.Contracts.Authorization;
using Boilerplate.Modules.Catalog.Contracts.v1.Products;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Boilerplate.Modules.Catalog.Features.v1.Products.RestoreProduct;

public static class RestoreProductEndpoint
{
    internal static RouteHandlerBuilder MapRestoreProductEndpoint(this IEndpointRouteBuilder endpoints)
    {
        return endpoints.MapPost("/products/{productId:guid}/restore",
                async (Guid productId, IMediator mediator, CancellationToken ct) =>
                    Results.Ok(await mediator.Send(new RestoreProductCommand(productId), ct)))
            .WithName("RestoreProduct")
            .WithSummary("Restore a soft-deleted product")
            .RequirePermission(CatalogPermissions.Products.Restore)
            .WithIdempotency();
    }
}
