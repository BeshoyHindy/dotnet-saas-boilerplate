using Boilerplate.Modules.Catalog.Contracts.Authorization;
using Boilerplate.BuildingBlocks.Shared.Identity.Authorization;
using Boilerplate.BuildingBlocks.Web.Idempotency;
using Boilerplate.Modules.Catalog.Contracts.v1.Brands;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Boilerplate.Modules.Catalog.Features.v1.Brands.CreateBrand;

public static class CreateBrandEndpoint
{
    internal static RouteHandlerBuilder MapCreateBrandEndpoint(this IEndpointRouteBuilder endpoints)
    {
        return endpoints.MapPost("/brands",
                async (CreateBrandCommand command, IMediator mediator, CancellationToken ct) =>
                    Results.Ok(await mediator.Send(command, ct)))
            .WithName("CreateBrand")
            .WithSummary("Create a brand")
            .RequirePermission(CatalogPermissions.Brands.Create)
            .WithIdempotency();
    }
}
