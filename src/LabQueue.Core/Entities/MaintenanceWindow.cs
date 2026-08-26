using NpgsqlTypes;

namespace LabQueue.Core.Entities;

public class MaintenanceWindow
{
    public Guid Id { get; set; }
    public Guid ResourceId { get; set; }
    public NpgsqlRange<DateTime> During { get; set; }
    public string? Reason { get; set; }
    public DateTime CreatedAt { get; set; }

    public Resource Resource { get; set; } = null!;
}
