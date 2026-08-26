using LabQueue.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LabQueue.Core.Data.Configurations;

public class MaintenanceWindowConfiguration : IEntityTypeConfiguration<MaintenanceWindow>
{
    public void Configure(EntityTypeBuilder<MaintenanceWindow> builder)
    {
        builder.ToTable("maintenance_windows", t => t.HasCheckConstraint(
            "maintenance_windows_during_bounds",
            "NOT isempty(during) AND lower_inc(during) AND NOT upper_inc(during)"));

        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id).HasColumnName("id");
        builder.Property(m => m.ResourceId).HasColumnName("resource_id");
        builder.Property(m => m.During).HasColumnName("during").HasColumnType("tstzrange").IsRequired();
        builder.Property(m => m.Reason).HasColumnName("reason").HasMaxLength(500);
        builder.Property(m => m.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");

        builder.HasOne(m => m.Resource).WithMany(r => r.MaintenanceWindows)
            .HasForeignKey(m => m.ResourceId).OnDelete(DeleteBehavior.Cascade);
    }
}
