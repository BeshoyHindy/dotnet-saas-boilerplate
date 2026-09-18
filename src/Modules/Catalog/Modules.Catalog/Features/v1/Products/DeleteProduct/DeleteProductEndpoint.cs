using Boilerplate.Modules.Catalog.Contracts.Authorization;
using Boilerplate.BuildingBlocks.Shared.Identity.Authorization;
using Boilerplate.Modules.Catalog.Contracts.v1.Products;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Boilerplate.Modules.Catalog.Features.v1.Products.DeleteProduct;

public static class DeleteProductEndpoint
{
    internal static RouteHandlerBuilder MapDeleteProductEndpoint(this IEndpointRouteBuilder endpoints)
    {
        return endpoints.MapDelete("/products/{productId:guid}",
                async (Guid productId, IMediator mediator, CancellationToken ct) =>
                {
                    await mediator.Send(new DeleteProductCommand(productId), ct);
                    return Results.NoContent();
                })
            .WithName("DeleteProduct")
            .WithSummary("Delete a product")
            .RequirePermission(CatalogPermissions.Products.Delete);
    }
}
