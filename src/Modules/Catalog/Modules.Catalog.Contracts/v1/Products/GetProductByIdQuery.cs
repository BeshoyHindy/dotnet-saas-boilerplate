using Boilerplate.Modules.Catalog.Contracts.Dtos;
using Mediator;

namespace Boilerplate.Modules.Catalog.Contracts.v1.Products;

public sealed record GetProductByIdQuery(Guid ProductId) : IQuery<ProductDto>;
