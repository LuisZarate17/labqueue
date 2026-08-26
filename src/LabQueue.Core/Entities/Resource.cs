using LabQueue.Core.Enums;

namespace LabQueue.Core.Entities;

public class Resource
{
    public Guid Id { get; set; }
    public string Code { get; set; } = null!;
    public string Name { get; set; } = null!;
    public string? Location { get; set; }
    public string? Description { get; set; }
    public Guid? RequiredCertificationId { get; set; }
    public ResourceStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }

    public Certification? RequiredCertification { get; set; }
    public ICollection<Reservation> Reservations { get; set; } = new List<Reservation>();
    public ICollection<MaintenanceWindow> MaintenanceWindows { get; set; } = new List<MaintenanceWindow>();
}
