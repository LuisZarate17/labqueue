using LabQueue.Core.Entities;

namespace LabQueue.Core.Services;

public enum BookingOutcome
{
    Created,
    ResourceNotFound,
    ResourceNotActive,
    InvalidWindow,
    CertificationRequired,
    MaintenanceConflict,
    ReservationConflict
}

public sealed record BookingResult(BookingOutcome Outcome, Reservation? Reservation, string? Detail)
{
    public static BookingResult Created(Reservation reservation) => new(BookingOutcome.Created, reservation, null);

    public static BookingResult Rejected(BookingOutcome outcome, string detail) => new(outcome, null, detail);
}

public enum CancellationOutcome
{
    Cancelled,
    NotFound,
    NotOwned,
    AlreadyCancelled
}
