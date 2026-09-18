using Mediator;

namespace Boilerplate.Modules.Catalog.Contracts.v1.Products;

public sealed record CreateProductCommand(
    string Sku,
    string Name,
    string? Description,
    Guid BrandId,
    Guid CategoryId,
    decimal PriceAmount,
    string PriceCurrency,
    int Stock) : ICommand<Guid>;
