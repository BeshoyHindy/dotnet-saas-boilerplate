using Boilerplate.Modules.Identity.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Boilerplate.Modules.Identity.Data.Configurations;

public class UserSessionConfiguration : IEntityTypeConfiguration<UserSession>
{
    public void Configure(EntityTypeBuilder<UserSession> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder
            .ToTable("UserSessions", IdentityModuleConstants.SchemaName)
            .HasKey(s => s.Id);

        builder
            .Property(s => s.UserId)
            .IsRequired()
            .HasMaxLength(450);

        builder
            .Property(s => s.RefreshTokenHash)
            .IsRequired()
            .HasMaxLength(256);

        builder
            .Property(s => s.PreviousTokenHash)
            .HasMaxLength(256);

        builder
            .Property(s => s.SecurityStamp)
            .IsRequired()
            .HasMaxLength(256);

        builder
            .Property(s => s.IpAddress)
            .IsRequired()
            .HasMaxLength(45);

        builder
            .Property(s => s.UserAgent)
            .IsRequired()
            .HasMaxLength(1024);

        builder
            .Property(s => s.DeviceType)
            .HasMaxLength(50);

        builder
            .Property(s => s.Browser)
            .HasMaxLength(100);

        builder
            .Property(s => s.BrowserVersion)
            .HasMaxLength(50);

        builder
            .Property(s => s.OperatingSystem)
            .HasMaxLength(100);

        builder
            .Property(s => s.OsVersion)
            .HasMaxLength(50);

        builder
            .Property(s => s.RevokedBy)
            .HasMaxLength(450);

        builder
            .Property(s => s.RevokedReason)
            .HasMaxLength(500);

        builder
            .Property(s => s.CreatedAt)
            .HasDefaultValueSql("CURRENT_TIMESTAMP");

        builder
            .HasOne(s => s.User)
            .WithMany()
            .HasForeignKey(s => s.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(s => s.UserId);
        // Unique: the hash is the identity of a live refresh token, so two rows sharing one would
        // mean the compare-and-set could rotate the wrong session.
        builder.HasIndex(s => s.RefreshTokenHash).IsUnique();
        // The reuse-detection lookup rides this one.
        builder.HasIndex(s => s.PreviousTokenHash);
        builder.HasIndex(s => s.ExpiresAt);
        builder.HasIndex(s => new { s.UserId, s.IsRevoked });
    }
}