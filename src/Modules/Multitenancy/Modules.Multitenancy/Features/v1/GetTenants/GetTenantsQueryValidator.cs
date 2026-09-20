using FluentValidation;
using Boilerplate.BuildingBlocks.Web.Validation;
using Boilerplate.Modules.Multitenancy.Contracts.v1.GetTenants;

namespace Boilerplate.Modules.Multitenancy.Features.v1.GetTenants;

public sealed class GetTenantsQueryValidator : AbstractValidator<GetTenantsQuery>
{
    public GetTenantsQueryValidator()
    {
        Include(new PagedQueryValidator<GetTenantsQuery>());
    }
}