using LabQueue.Core.Data.Converters;
using LabQueue.Core.Entities;
using LabQueue.Core.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LabQueue.Core.Data.Configurations;

public class ReservationConfiguration : IEntityTypeConfiguration<Reservation>
{
    public void Configure(EntityTypeBuilder<Reservation> builder)
    {
        builder.ToTable("reservations", t =>
        {
            t.HasCheckConstraint(
                "reservations_status_check", "status IN ('confirmed', 'cancelled')");
            t.HasCheckConstraint(
                "reservations_during_bounds",
                "NOT isempty(during) AND lower_inc(during) AND NOT upper_inc(during)");
        });

        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).HasColumnName("id");
        builder.Property(r => r.ResourceId).HasColumnName("resource_id");
        builder.Property(r => r.UserId).HasColumnName("user_id");
        builder.Property(r => r.During).HasColumnName("during").HasColumnType("tstzrange").IsRequired();
        builder.Property(r => r.Status).HasColumnName("status")
            .HasConversion(LowercaseEnumConverter.For<ReservationStatus>()).IsRequired();
        builder.Property(r => r.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
        builder.Property(r => r.CancelledAt).HasColumnName("cancelled_at");

        builder.HasOne(r => r.Resource).WithMany(r => r.Reservations)
            .HasForeignKey(r => r.ResourceId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(r => r.User).WithMany(u => u.Reservations)
            .HasForeignKey(r => r.UserId).OnDelete(DeleteBehavior.Restrict);
    }
}
