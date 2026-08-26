using LabQueue.Core.Enums;

namespace LabQueue.Core.Entities;

public class User
{
    public Guid Id { get; set; }
    public string Email { get; set; } = null!;
    public string PasswordHash { get; set; } = null!;
    public string DisplayName { get; set; } = null!;
    public UserRole Role { get; set; }
    public DateTime CreatedAt { get; set; }

    public ICollection<UserCertification> Certifications { get; set; } = new List<UserCertification>();
    public ICollection<Reservation> Reservations { get; set; } = new List<Reservation>();
}
