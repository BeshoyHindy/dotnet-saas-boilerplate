using System.Net;
using Boilerplate.BuildingBlocks.Core.Exceptions;
using Boilerplate.Modules.Catalog.Contracts.v1.Categories;
using Boilerplate.Modules.Catalog.Data;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace Boilerplate.Modules.Catalog.Features.v1.Categories.DeleteCategory;

public sealed class DeleteCategoryCommandHandler(CatalogDbContext dbContext)
    : ICommandHandler<DeleteCategoryCommand, Unit>
{
    public async ValueTask<Unit> Handle(DeleteCategoryCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var category = await dbContext.Categories
            .FirstOrDefaultAsync(c => c.Id == command.CategoryId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new NotFoundException($"Category {command.CategoryId} not found.");

        bool hasChildren = await dbContext.Categories
            .AnyAsync(c => c.ParentCategoryId == category.Id, cancellationToken)
            .ConfigureAwait(false);
        if (hasChildren)
        {
            throw new CustomException(
                "Cannot delete a category that has child categories. Move or remove the children first.",
                (IEnumerable<string>?)null,
                HttpStatusCode.Conflict);
        }

        dbContext.Categories.Remove(category);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Unit.Value;
    }
}
