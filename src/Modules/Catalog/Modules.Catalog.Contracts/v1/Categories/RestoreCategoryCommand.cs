using Mediator;

namespace Boilerplate.Modules.Catalog.Contracts.v1.Categories;

public sealed record RestoreCategoryCommand(Guid CategoryId) : ICommand<Guid>;
