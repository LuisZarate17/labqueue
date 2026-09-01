using LabQueue.Core.Data;
using LabQueue.Core.Entities;
using LabQueue.Core.Enums;
using LabQueue.Core.Time;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace LabQueue.Core.Services;

public sealed class ReservationService(LabQueueDbContext db)
{
    public async Task<BookingResult> BookAsync(
        Guid userId,
        Guid resourceId,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken ct = default)
    {
        var resource = await db.Resources.FirstOrDefaultAsync(r => r.Id == resourceId, ct);

        if (resource is null)
        {
            return BookingResult.Rejected(
                BookingOutcome.ResourceNotFound,
                $"No resource exists with id {resourceId}.");
        }

        if (resource.Status != ResourceStatus.Active)
        {
            return BookingResult.Rejected(
                BookingOutcome.ResourceNotActive,
                $"Resource {resource.Code} is retired and cannot be booked.");
        }

        if (to <= from)
        {
            return BookingResult.Rejected(
                BookingOutcome.InvalidWindow,
                "The end of the window must be later than its start.");
        }

        if (!TimeWindow.HasValidDuration(from, to))
        {
            return BookingResult.Rejected(
                BookingOutcome.InvalidWindow,
                $"A reservation must last between {TimeWindow.MinimumDuration.TotalMinutes:0} minutes "
                + $"and {TimeWindow.MaximumDuration.TotalHours:0} hours.");
        }

        var during = TimeWindow.ClosedOpen(from, to);

        if (resource.RequiredCertificationId is { } requiredCertificationId)
        {
            var now = DateTime.UtcNow;
            var holdsCertification = await db.UserCertifications.AnyAsync(
                uc => uc.UserId == userId
                      && uc.CertificationId == requiredCertificationId
                      && (uc.ExpiresAt == null || uc.ExpiresAt > now),
                ct);

            if (!holdsCertification)
            {
                return BookingResult.Rejected(
                    BookingOutcome.CertificationRequired,
                    $"Resource {resource.Code} requires a current certification that you do not hold.");
            }
        }

        var maintenance = await db.MaintenanceWindows.FirstOrDefaultAsync(
            m => m.ResourceId == resourceId && m.During.Overlaps(during), ct);

        if (maintenance is not null)
        {
            return BookingResult.Rejected(
                BookingOutcome.MaintenanceConflict,
                $"Resource {resource.Code} is under maintenance from "
                + $"{maintenance.During.LowerBound:u} to {maintenance.During.UpperBound:u}.");
        }

        var conflict = await db.Reservations.FirstOrDefaultAsync(
            r => r.ResourceId == resourceId
                 && r.Status == ReservationStatus.Confirmed
                 && r.During.Overlaps(during),
            ct);

        if (conflict is not null)
        {
            return BookingResult.Rejected(
                BookingOutcome.ReservationConflict,
                $"Resource {resource.Code} is already booked from "
                + $"{conflict.During.LowerBound:u} to {conflict.During.UpperBound:u}.");
        }

        var reservation = new Reservation
        {
            Id = Guid.CreateVersion7(),
            ResourceId = resourceId,
            UserId = userId,
            During = during,
            Status = ReservationStatus.Confirmed,
            CreatedAt = DateTime.UtcNow
        };

        db.Reservations.Add(reservation);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.ExclusionViolation
        })
        {
            // The overlap check above is a SELECT followed by an INSERT with nothing
            // holding the gap. A concurrent caller can pass the same check and insert
            // first, so the check being clean does not mean the slot is still free by the
            // time this row lands. reservations_no_overlap is what actually enforces it;
            // this branch reports the loss the same way the check above would have.
            db.Entry(reservation).State = EntityState.Detached;

            return BookingResult.Rejected(
                BookingOutcome.ReservationConflict,
                $"Resource {resource.Code} was booked for an overlapping window by another "
                + "request while this one was in flight.");
        }
        catch (Exception e) when (IsDeadlock(e))
        {
            // Reached only after the retry policy has given up. Postgres resolves contention
            // on reservations_no_overlap as an exclusion violation most of the time, but
            // under enough simultaneous inserters the wait-for graph cycles and it resolves
            // some of them as deadlocks instead. Those say nothing about whether the slot is
            // free — this transaction was killed before it could find out — so this is not a
            // conflict and must not be reported as one.
            db.Entry(reservation).State = EntityState.Detached;

            return BookingResult.Rejected(
                BookingOutcome.DeadlockAborted,
                $"The booking for {resource.Code} could not be confirmed because too many "
                + "requests were competing for that window. The slot may still be free; "
                + "retry the request.");
        }

        return BookingResult.Created(reservation);
    }

    /// <summary>
    /// Whether a 40P01 deadlock is anywhere in the exception's chain.
    ///
    /// It never arrives as a bare <see cref="DbUpdateException"/>, which is why the clause
    /// above cannot catch it however many SqlStates are added to it. Npgsql classifies
    /// deadlock as transient, so EF Core hands it back wrapped, and the wrapper depends on
    /// configuration: <c>RetryLimitExceededException</c> once a retrying strategy is out of
    /// attempts, and <c>InvalidOperationException("...likely due to a transient failure")</c>
    /// when no retry policy is configured at all.
    ///
    /// Matching the SqlState rather than any wrapper type is what keeps the catch narrow.
    /// Nothing without a 40P01 in its chain is caught, so ordinary failures still surface
    /// as 500s.
    /// </summary>
    private static bool IsDeadlock(Exception exception)
    {
        for (var e = exception; e is not null; e = e.InnerException)
        {
            if (e is PostgresException { SqlState: PostgresErrorCodes.DeadlockDetected })
            {
                return true;
            }
        }

        return false;
    }

    public async Task<CancellationOutcome> CancelAsync(
        Guid reservationId,
        Guid userId,
        bool callerIsAdmin,
        CancellationToken ct = default)
    {
        var reservation = await db.Reservations.FirstOrDefaultAsync(r => r.Id == reservationId, ct);

        if (reservation is null)
        {
            return CancellationOutcome.NotFound;
        }

        if (reservation.UserId != userId && !callerIsAdmin)
        {
            return CancellationOutcome.NotOwned;
        }

        if (reservation.Status == ReservationStatus.Cancelled)
        {
            return CancellationOutcome.AlreadyCancelled;
        }

        reservation.Status = ReservationStatus.Cancelled;
        reservation.CancelledAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        return CancellationOutcome.Cancelled;
    }
}
