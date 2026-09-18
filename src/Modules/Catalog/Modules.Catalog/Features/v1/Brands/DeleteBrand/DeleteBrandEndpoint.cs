using Boilerplate.Modules.Catalog.Contracts.Authorization;
using Boilerplate.BuildingBlocks.Shared.Identity.Authorization;
using Boilerplate.Modules.Catalog.Contracts.v1.Brands;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Boilerplate.Modules.Catalog.Features.v1.Brands.DeleteBrand;

public static class DeleteBrandEndpoint
{
    internal static RouteHandlerBuilder MapDeleteBrandEndpoint(this IEndpointRouteBuilder endpoints)
    {
        return endpoints.MapDelete("/brands/{brandId:guid}",
                async (Guid brandId, IMediator mediator, CancellationToken ct) =>
                {
                    await mediator.Send(new DeleteBrandCommand(brandId), ct);
                    return Results.NoContent();
                })
            .WithName("DeleteBrand")
            .WithSummary("Delete a brand")
            .RequirePermission(CatalogPermissions.Brands.Delete);
    }
}
