using Mediator;

namespace Boilerplate.Modules.Catalog.Contracts.v1.Brands;

public sealed record CreateBrandCommand(
    string Name,
    string? Description = null,
    string? LogoUrl = null) : ICommand<Guid>;
