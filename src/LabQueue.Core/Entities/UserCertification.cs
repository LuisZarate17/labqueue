namespace LabQueue.Core.Entities;

public class UserCertification
{
    public Guid UserId { get; set; }
    public Guid CertificationId { get; set; }
    public DateTime GrantedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }

    public User User { get; set; } = null!;
    public Certification Certification { get; set; } = null!;
}
