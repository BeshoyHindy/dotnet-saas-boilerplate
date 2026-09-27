using Finbuckle.MultiTenant.EntityFrameworkCore.Extensions;
using Boilerplate.Modules.Identity.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Boilerplate.Modules.Identity.Data;

public class ApplicationUserConfig : IEntityTypeConfiguration<AppUser>
{
    public void Configure(EntityTypeBuilder<AppUser> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder
            .ToTable("Users", IdentityModuleConstants.SchemaName)
            .IsMultiTenant();

        builder
            .Property(u => u.ObjectId)
                .HasMaxLength(256);

        // E-mail uniqueness is enforced by the database, not by a validator's read. ASP.NET Identity
        // maps NormalizedEmail as a plain lookup index and enforces `RequireUniqueEmail` with a query
        // before the insert, so two concurrent sign-ups with the same address both passed. The index
        // is widened with TenantId by hand because Finbuckle only adjusts indexes that are ALREADY
        // unique — that is how UserNameIndex became (NormalizedUserName, TenantId), and this mirrors
        // it, so an address is free again in every other tenant (ADR-0002).
        //
        // Postgres treats NULLs as distinct in a unique index, so users with no e-mail are unaffected.
        var lookupIndex = builder.Metadata.GetIndexes().FirstOrDefault(index =>
            index.Properties.Count == 1 &&
            string.Equals(index.Properties[0].Name, nameof(AppUser.NormalizedEmail), StringComparison.Ordinal));

        if (lookupIndex is not null)
        {
            builder.Metadata.RemoveIndex(lookupIndex);
        }

        builder
            .HasIndex(nameof(AppUser.NormalizedEmail), "TenantId")
            .HasDatabaseName("EmailIndex")
            .IsUnique();

        // SearchUsers matches ILIKE '%term%' on these four columns. pg_trgm GIN indexes turn that
        // from a scan of the tenant's users into a probe (mirrors AuditRecordConfiguration). Named,
        // so they never merge with an Identity-defined index on the same column.
        HasTrigramIndex(builder, nameof(AppUser.FirstName), "IX_Users_FirstName_trgm");
        HasTrigramIndex(builder, nameof(AppUser.LastName), "IX_Users_LastName_trgm");
        HasTrigramIndex(builder, nameof(AppUser.Email), "IX_Users_Email_trgm");
        HasTrigramIndex(builder, nameof(AppUser.UserName), "IX_Users_UserName_trgm");
    }

    private static void HasTrigramIndex(EntityTypeBuilder<AppUser> builder, string property, string name) =>
        builder.HasIndex([property], name)
            .HasMethod("gin")
            .HasOperators("gin_trgm_ops");
}

public class ApplicationRoleConfig : IEntityTypeConfiguration<AppRole>
{
    public void Configure(EntityTypeBuilder<AppRole> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder
            .ToTable("Roles", IdentityModuleConstants.SchemaName)
            .IsMultiTenant()
                .AdjustUniqueIndexes();
    }
}

public class ApplicationRoleClaimConfig : IEntityTypeConfiguration<AppRoleClaim>
{
    public void Configure(EntityTypeBuilder<AppRoleClaim> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder
            .ToTable("RoleClaims", IdentityModuleConstants.SchemaName)
            .IsMultiTenant();
    }
}

public class IdentityUserRoleConfig : IEntityTypeConfiguration<IdentityUserRole<string>>
{
    public void Configure(EntityTypeBuilder<IdentityUserRole<string>> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder
            .ToTable("UserRoles", IdentityModuleConstants.SchemaName)
            .IsMultiTenant();
    }
}

public class IdentityUserClaimConfig : IEntityTypeConfiguration<IdentityUserClaim<string>>
{
    public void Configure(EntityTypeBuilder<IdentityUserClaim<string>> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder
            .ToTable("UserClaims", IdentityModuleConstants.SchemaName)
            .IsMultiTenant();
    }
}

public class IdentityUserLoginConfig : IEntityTypeConfiguration<IdentityUserLogin<string>>
{
    public void Configure(EntityTypeBuilder<IdentityUserLogin<string>> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder
            .ToTable("UserLogins", IdentityModuleConstants.SchemaName)
            .IsMultiTenant();
    }
}

public class IdentityUserTokenConfig : IEntityTypeConfiguration<IdentityUserToken<string>>
{
    public void Configure(EntityTypeBuilder<IdentityUserToken<string>> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder
            .ToTable("UserTokens", IdentityModuleConstants.SchemaName)
            .IsMultiTenant();
    }
}