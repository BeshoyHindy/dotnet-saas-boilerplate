using Mediator;

namespace Boilerplate.Modules.Catalog.Contracts.v1.Brands;

public sealed record DeleteBrandCommand(Guid BrandId) : ICommand<Unit>;
