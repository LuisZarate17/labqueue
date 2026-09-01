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
    ReservationConflict,

    /// <summary>
    /// Postgres killed the insert as a deadlock victim and the retries did not settle it.
    /// Deliberately not <see cref="ReservationConflict"/>: no overlapping row was observed,
    /// so the window may well still be free, and counting it as a conflict would both lie to
    /// the caller and inflate the instrument that measures how often the overlap check bites.
    /// </summary>
    DeadlockAborted
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
