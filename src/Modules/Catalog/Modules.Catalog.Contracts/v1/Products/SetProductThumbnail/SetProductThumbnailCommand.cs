using Mediator;

namespace Boilerplate.Modules.Catalog.Contracts.v1.Products.SetProductThumbnail;

/// <summary>Promote an existing image to thumbnail (cover). Clears the flag on every other image.</summary>
public sealed record SetProductThumbnailCommand(Guid ProductId, Guid ImageId) : ICommand<Unit>;
