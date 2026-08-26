using LabQueue.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LabQueue.Core.Data.Configurations;

public class UserCertificationConfiguration : IEntityTypeConfiguration<UserCertification>
{
    public void Configure(EntityTypeBuilder<UserCertification> builder)
    {
        builder.ToTable("user_certifications");

        builder.HasKey(uc => new { uc.UserId, uc.CertificationId });
        builder.Property(uc => uc.UserId).HasColumnName("user_id");
        builder.Property(uc => uc.CertificationId).HasColumnName("certification_id");
        builder.Property(uc => uc.GrantedAt).HasColumnName("granted_at").HasDefaultValueSql("now()");
        builder.Property(uc => uc.ExpiresAt).HasColumnName("expires_at");

        builder.HasOne(uc => uc.User).WithMany(u => u.Certifications)
            .HasForeignKey(uc => uc.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(uc => uc.Certification).WithMany(c => c.Holders)
            .HasForeignKey(uc => uc.CertificationId).OnDelete(DeleteBehavior.Cascade);
    }
}
