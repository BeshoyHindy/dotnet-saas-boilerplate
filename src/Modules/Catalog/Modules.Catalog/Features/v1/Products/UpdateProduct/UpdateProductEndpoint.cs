using Boilerplate.Modules.Catalog.Contracts.Authorization;
using Boilerplate.BuildingBlocks.Shared.Identity.Authorization;
using Boilerplate.Modules.Catalog.Contracts.v1.Products;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Boilerplate.Modules.Catalog.Features.v1.Products.UpdateProduct;

public static class UpdateProductEndpoint
{
    internal static RouteHandlerBuilder MapUpdateProductEndpoint(this IEndpointRouteBuilder endpoints)
    {
        return endpoints.MapPut("/products/{productId:guid}",
                async (Guid productId, UpdateProductCommand body, IMediator mediator, CancellationToken ct) =>
                {
                    ArgumentNullException.ThrowIfNull(body);
                    var command = body with { ProductId = productId };
                    return Results.Ok(await mediator.Send(command, ct));
                })
            .WithName("UpdateProduct")
            .WithSummary("Update a product")
            .RequirePermission(CatalogPermissions.Products.Update);
    }
}
