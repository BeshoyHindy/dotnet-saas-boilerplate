using System.Net;
using Boilerplate.BuildingBlocks.Core.Exceptions;
using Boilerplate.Modules.Catalog.Contracts.v1.Brands;
using Boilerplate.Modules.Catalog.Data;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace Boilerplate.Modules.Catalog.Features.v1.Brands.UpdateBrand;

public sealed class UpdateBrandCommandHandler(CatalogDbContext dbContext)
    : ICommandHandler<UpdateBrandCommand, Guid>
{
    public async ValueTask<Guid> Handle(UpdateBrandCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var brand = await dbContext.Brands
            .FirstOrDefaultAsync(b => b.Id == command.BrandId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new NotFoundException($"Brand {command.BrandId} not found.");

        brand.Update(command.Name, command.Description, command.LogoUrl);

        bool slugTaken = await dbContext.Brands
            .AnyAsync(b => b.Slug == brand.Slug && b.Id != brand.Id, cancellationToken)
            .ConfigureAwait(false);
        if (slugTaken)
        {
            throw new CustomException(
                $"Another brand with name '{command.Name}' already exists.",
                (IEnumerable<string>?)null,
                HttpStatusCode.Conflict);
        }

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return brand.Id;
    }
}
