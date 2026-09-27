using Finbuckle.MultiTenant.EntityFrameworkCore.Extensions;
using Boilerplate.Modules.Files.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Boilerplate.Modules.Files.Data.Configurations;

public sealed class FileAssetConfiguration : IEntityTypeConfiguration<FileAsset>
{
    public void Configure(EntityTypeBuilder<FileAsset> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("FileAssets");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.OwnerType).IsRequired().HasMaxLength(64);
        builder.Property(x => x.OwnerId);
        builder.Property(x => x.FileName).IsRequired().HasMaxLength(260);
        builder.Property(x => x.OriginalFileName).IsRequired().HasMaxLength(260);
        builder.Property(x => x.ContentType).IsRequired().HasMaxLength(128);
        builder.Property(x => x.SizeBytes).IsRequired();
        builder.Property(x => x.StorageKey).IsRequired().HasMaxLength(512);
        builder.Property(x => x.Visibility).HasConversion<int>().IsRequired();
        builder.Property(x => x.Status).HasConversion<int>().IsRequired();
        builder.Property(x => x.ScanStatus).HasConversion<int>().IsRequired();
        builder.Property(x => x.UploadDeadline);
        builder.Property(x => x.CreatedByUserId).IsRequired().HasMaxLength(64);
        builder.Property(x => x.CreatedAtUtc).IsRequired();
        builder.Property(x => x.UpdatedAtUtc);
        builder.Property(x => x.IsDeleted).IsRequired();
        builder.Property(x => x.DeletedOnUtc);
        builder.Property(x => x.DeletedBy).HasMaxLength(64);

        // Every tenant shares these tables; isolation is the TenantId column Finbuckle adds and the
        // default-on query filter. A lookup index leads with TenantId only where its own columns are
        // not selective without it (IX_FileAsset_Shared below).
        builder.HasIndex(x => new { x.OwnerType, x.OwnerId })
            .HasDatabaseName("IX_FileAsset_Owner");
        builder.HasIndex(x => x.Status)
            .HasDatabaseName("IX_FileAsset_Status");
        builder.HasIndex(x => new { x.IsDeleted, x.DeletedOnUtc })
            .HasDatabaseName("IX_FileAsset_Deletion");
        // Unique on StorageKey across live rows only — a soft-deleted row's key should not block
        // a subsequent upload that happens to choose the same path (rare, but possible).
        builder.HasIndex(x => x.StorageKey)
            .IsUnique()
            .HasFilter("\"IsDeleted\" = FALSE")
            .HasDatabaseName("UX_FileAsset_StorageKey");

        // Declared here rather than left to BaseDbContext's default so the TenantId column exists
        // for IX_FileAsset_Shared. Same result as the default: filtered, unique indexes widened.
        builder.IsMultiTenant().AdjustUniqueIndexes();

        // ListMyFiles: WHERE CreatedByUserId = @me AND Status = Available ORDER BY CreatedAtUtc DESC.
        // A user id is already selective (and unique across tenants), so it leads.
        builder.HasIndex(x => new { x.CreatedByUserId, x.Status, x.CreatedAtUtc })
            .HasDatabaseName("IX_FileAsset_CreatedBy");

        // ListSharedFiles: WHERE Visibility = Public AND Status = Available AND OwnerType IN (…)
        // ORDER BY CreatedAtUtc DESC. None of those columns is selective alone; the tenant is.
        builder.HasIndex("TenantId", nameof(FileAsset.Visibility), nameof(FileAsset.Status), nameof(FileAsset.OwnerType), nameof(FileAsset.CreatedAtUtc))
            .HasDatabaseName("IX_FileAsset_Shared");

        builder.Ignore(x => x.DomainEvents);
    }
}
