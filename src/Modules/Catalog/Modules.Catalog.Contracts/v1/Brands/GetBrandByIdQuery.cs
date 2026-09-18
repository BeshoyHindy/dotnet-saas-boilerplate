using Boilerplate.Modules.Catalog.Contracts.Dtos;
using Mediator;

namespace Boilerplate.Modules.Catalog.Contracts.v1.Brands;

public sealed record GetBrandByIdQuery(Guid BrandId) : IQuery<BrandDto>;
