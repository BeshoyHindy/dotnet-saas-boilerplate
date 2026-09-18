using Boilerplate.BuildingBlocks.Shared.Persistence;
using Boilerplate.Modules.Catalog.Contracts.Dtos;
using Mediator;

namespace Boilerplate.Modules.Catalog.Contracts.v1.Products;

public sealed record ListTrashedProductsQuery(int PageNumber = 1, int PageSize = 20)
    : IQuery<PagedResponse<ProductDto>>;
