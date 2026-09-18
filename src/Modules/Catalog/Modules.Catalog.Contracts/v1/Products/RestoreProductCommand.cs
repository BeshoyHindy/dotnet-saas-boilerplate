using Mediator;

namespace Boilerplate.Modules.Catalog.Contracts.v1.Products;

public sealed record RestoreProductCommand(Guid ProductId) : ICommand<Guid>;
