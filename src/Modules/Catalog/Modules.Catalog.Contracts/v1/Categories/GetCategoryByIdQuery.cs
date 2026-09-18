using Boilerplate.Modules.Catalog.Contracts.Dtos;
using Mediator;

namespace Boilerplate.Modules.Catalog.Contracts.v1.Categories;

public sealed record GetCategoryByIdQuery(Guid CategoryId) : IQuery<CategoryDto>;
