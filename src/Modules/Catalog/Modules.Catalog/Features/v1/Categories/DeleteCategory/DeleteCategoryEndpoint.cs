using Boilerplate.Modules.Catalog.Contracts.Authorization;
using Boilerplate.BuildingBlocks.Shared.Identity.Authorization;
using Boilerplate.Modules.Catalog.Contracts.v1.Categories;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Boilerplate.Modules.Catalog.Features.v1.Categories.DeleteCategory;

public static class DeleteCategoryEndpoint
{
    internal static RouteHandlerBuilder MapDeleteCategoryEndpoint(this IEndpointRouteBuilder endpoints)
    {
        return endpoints.MapDelete("/categories/{categoryId:guid}",
                async (Guid categoryId, IMediator mediator, CancellationToken ct) =>
                {
                    await mediator.Send(new DeleteCategoryCommand(categoryId), ct);
                    return Results.NoContent();
                })
            .WithName("DeleteCategory")
            .WithSummary("Delete a category")
            .RequirePermission(CatalogPermissions.Categories.Delete);
    }
}
