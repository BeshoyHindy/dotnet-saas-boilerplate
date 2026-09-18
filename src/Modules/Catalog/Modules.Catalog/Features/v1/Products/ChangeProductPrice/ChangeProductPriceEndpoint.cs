using Boilerplate.Modules.Catalog.Contracts.Authorization;
using Boilerplate.BuildingBlocks.Shared.Identity.Authorization;
using Boilerplate.Modules.Catalog.Contracts.v1.Products;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Boilerplate.Modules.Catalog.Features.v1.Products.ChangeProductPrice;

public static class ChangeProductPriceEndpoint
{
    internal static RouteHandlerBuilder MapChangeProductPriceEndpoint(this IEndpointRouteBuilder endpoints)
    {
        return endpoints.MapPatch("/products/{productId:guid}/price",
                async (Guid productId, ChangeProductPriceCommand body, IMediator mediator, CancellationToken ct) =>
                {
                    ArgumentNullException.ThrowIfNull(body);
                    var command = body with { ProductId = productId };
                    return Results.Ok(await mediator.Send(command, ct));
                })
            .WithName("ChangeProductPrice")
            .WithSummary("Change a product's price")
            .RequirePermission(CatalogPermissions.Products.Update);
    }
}
