using LabQueue.Core.Data.Converters;
using LabQueue.Core.Entities;
using LabQueue.Core.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LabQueue.Core.Data.Configurations;

public class ResourceConfiguration : IEntityTypeConfiguration<Resource>
{
    public void Configure(EntityTypeBuilder<Resource> builder)
    {
        builder.ToTable("resources", t => t.HasCheckConstraint(
            "resources_status_check", "status IN ('active', 'retired')"));

        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).HasColumnName("id");
        builder.Property(r => r.Code).HasColumnName("code").HasMaxLength(50).IsRequired();
        builder.Property(r => r.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
        builder.Property(r => r.Location).HasColumnName("location").HasMaxLength(200);
        builder.Property(r => r.Description).HasColumnName("description");
        builder.Property(r => r.RequiredCertificationId).HasColumnName("required_certification_id");
        builder.Property(r => r.Status).HasColumnName("status")
            .HasConversion(LowercaseEnumConverter.For<ResourceStatus>()).IsRequired();
        builder.Property(r => r.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");

        builder.HasIndex(r => r.Code).IsUnique().HasDatabaseName("ix_resources_code");

        builder.HasOne(r => r.RequiredCertification).WithMany()
            .HasForeignKey(r => r.RequiredCertificationId).OnDelete(DeleteBehavior.Restrict);
    }
}
