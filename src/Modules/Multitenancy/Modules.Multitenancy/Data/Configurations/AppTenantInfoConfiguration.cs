using Boilerplate.BuildingBlocks.Shared.Multitenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Boilerplate.Modules.Multitenancy.Data.Configurations;

public class AppTenantInfoConfiguration : IEntityTypeConfiguration<AppTenantInfo>
{
    public void Configure(EntityTypeBuilder<AppTenantInfo> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("Tenants", MultitenancyConstants.Schema);
    }
}
