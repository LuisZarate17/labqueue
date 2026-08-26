using LabQueue.Core.Enums;
using NpgsqlTypes;

namespace LabQueue.Core.Entities;

public class Reservation
{
    public Guid Id { get; set; }
    public Guid ResourceId { get; set; }
    public Guid UserId { get; set; }
    public NpgsqlRange<DateTime> During { get; set; }
    public ReservationStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? CancelledAt { get; set; }

    public Resource Resource { get; set; } = null!;
    public User User { get; set; } = null!;
}
