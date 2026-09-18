using Boilerplate.Modules.Catalog.Contracts.Dtos;
using Boilerplate.Modules.Catalog.Domain;

namespace Boilerplate.Modules.Catalog.Features.v1.Products;

internal static class ProductMappings
{
    public static ProductDto ToDto(this Product p) => new(
        p.Id,
        p.Sku,
        p.Name,
        p.Slug,
        p.Description,
        p.BrandId,
        p.CategoryId,
        new MoneyDto(p.Price.Amount, p.Price.Currency),
        p.Stock,
        p.IsActive,
        p.ThumbnailUrl,
        p.Images
            .OrderBy(i => i.SortOrder)
            .Select(i => new ProductImageDto(i.Id, i.FileAssetId, i.Url, i.IsThumbnail, i.SortOrder, i.CreatedAtUtc))
            .ToList(),
        p.CreatedAtUtc,
        p.UpdatedAtUtc,
        p.DeletedOnUtc,
        p.DeletedBy);
}
