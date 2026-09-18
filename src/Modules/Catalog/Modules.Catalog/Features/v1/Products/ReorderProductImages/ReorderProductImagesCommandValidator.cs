using FluentValidation;
using Boilerplate.Modules.Catalog.Contracts.v1.Products.ReorderProductImages;

namespace Boilerplate.Modules.Catalog.Features.v1.Products.ReorderProductImages;

public sealed class ReorderProductImagesCommandValidator : AbstractValidator<ReorderProductImagesCommand>
{
    public ReorderProductImagesCommandValidator()
    {
        RuleFor(x => x.ProductId).NotEmpty();
        RuleFor(x => x.OrderedImageIds).NotNull();
    }
}
