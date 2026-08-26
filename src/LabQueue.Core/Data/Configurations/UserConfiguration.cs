using LabQueue.Core.Data.Converters;
using LabQueue.Core.Entities;
using LabQueue.Core.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LabQueue.Core.Data.Configurations;

public class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("users", t => t.HasCheckConstraint(
            "users_role_check", "role IN ('member', 'admin')"));

        builder.HasKey(u => u.Id);
        builder.Property(u => u.Id).HasColumnName("id");
        builder.Property(u => u.Email).HasColumnName("email").HasColumnType("citext").IsRequired();
        builder.Property(u => u.PasswordHash).HasColumnName("password_hash").IsRequired();
        builder.Property(u => u.DisplayName).HasColumnName("display_name").HasMaxLength(200).IsRequired();
        builder.Property(u => u.Role).HasColumnName("role")
            .HasConversion(LowercaseEnumConverter.For<UserRole>()).IsRequired();
        builder.Property(u => u.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");

        builder.HasIndex(u => u.Email).IsUnique().HasDatabaseName("ix_users_email");
    }
}
