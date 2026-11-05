using LabQueue.Core.Data;
using LabQueue.Core.Entities;
using LabQueue.Core.Enums;
using LabQueue.Core.Time;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace LabQueue.Core.Services;

public sealed class ReservationService(LabQueueDbContext db)
{
    /// <summary>
    /// How many times a deadlock victim re-attempts the insert before giving up. Small on
    /// purpose: each attempt re-reads first, so the winner is normally visible by the second
    /// pass, and a larger number would only keep losers contending for a slot that is gone.
    /// </summary>
    private const int MaximumDeadlockAttempts = 3;

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

        // Unlocked, and only a fast path: a slot that is already taken is refused here without
        // queueing for the resource's lock. The check that counts is repeated under the lock.
        if (await FindOverlapAsync(resourceId, during, ct) is { } conflict)
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

        for (var attempt = 0; ; attempt++)
        {
            // Lock, re-check and insert are one transaction, so the lock is held until the
            // insert commits or rolls back and the next booking for this resource cannot read
            // the slot in between. Declared with a block rather than a using declaration so
            // that a deadlock victim's transaction is rolled back, releasing its locks, before
            // the backoff below rather than after it.
            await using (var transaction = await db.Database.BeginTransactionAsync(ct))
            {
                try
                {
                    await LockResourceAsync(resourceId, ct);

                    // Every attempt re-reads under the lock, and that includes a deadlock
                    // victim's retry: the re-read before re-insert is what lets a victim find
                    // the winner and leave without contending again (DECISIONS.md section 7).
                    // Under READ COMMITTED this statement takes a fresh snapshot after the lock
                    // is granted, so it sees whatever the previous holder committed.
                    if (await FindOverlapAsync(resourceId, during, ct) is { } winner)
                    {
                        return BookingResult.Rejected(
                            BookingOutcome.ReservationConflict,
                            $"Resource {resource.Code} is already booked from "
                            + $"{winner.During.LowerBound:u} to {winner.During.UpperBound:u}.");
                    }

                    db.Reservations.Add(reservation);
                    await db.SaveChangesAsync(ct);
                    await transaction.CommitAsync(ct);
                    return BookingResult.Created(reservation);
                }
                catch (DbUpdateException e) when (e.InnerException is PostgresException
                {
                    SqlState: PostgresErrorCodes.ExclusionViolation
                })
                {
                    // The lock serialises bookings that come through here; it does not make the
                    // check above authoritative. reservations_no_overlap is still what enforces
                    // the rule, against every writer including ones that never take the lock,
                    // and this branch reports its refusal the same way the check would have.
                    db.Entry(reservation).State = EntityState.Detached;

                    return BookingResult.Rejected(
                        BookingOutcome.ReservationConflict,
                        $"Resource {resource.Code} was booked for an overlapping window by another "
                        + "request while this one was in flight.");
                }
                catch (Exception e) when (IsDeadlock(e))
                {
                    // With the lock in place bookings for one resource no longer meet at the
                    // constraint, so this should not fire from contention between them. It is
                    // kept unchanged for anything that does still deadlock - a writer outside
                    // this path, or a lock order nobody has thought of yet. A victim learns
                    // nothing about the slot: it was aborted before it could find out whether
                    // the window was taken.
                    db.Entry(reservation).State = EntityState.Detached;
                }
            }

            if (attempt == MaximumDeadlockAttempts)
            {
                return BookingResult.Rejected(
                    BookingOutcome.DeadlockAborted,
                    $"The booking for {resource.Code} could not be confirmed because too "
                    + "many requests were competing for that window. The slot may still be "
                    + "free; retry the request.");
            }

            // Jittered, so victims do not wake together and rebuild the pile-up they were
            // just released from.
            await Task.Delay(Random.Shared.Next(20, 60) * (attempt + 1), ct);
        }
    }

    /// <summary>
    /// Takes a row lock on the resource for the rest of the current transaction, so bookings
    /// for one resource run their check and insert one at a time while bookings for other
    /// resources are untouched.
    ///
    /// This is contention control, not the guarantee. Without it, simultaneous inserts for
    /// one slot all reach reservations_no_overlap at once; each waits on the others, and once
    /// those waits form cycles Postgres can only break about one per deadlock_timeout. With
    /// nothing committed, every victim's re-read finds the slot still free and it re-inserts,
    /// so the queue never drains (DECISIONS.md section 9).
    ///
    /// FOR NO KEY UPDATE rather than FOR UPDATE because the foreign-key check on every insert
    /// referencing this row takes FOR KEY SHARE, which FOR UPDATE would block and FOR NO KEY
    /// UPDATE does not. A row lock rather than pg_advisory_xact_lock because the lock is then
    /// the resource's own row: no hashing a uuid into a bigint key space where two resources
    /// could collide, and it shows up as an ordinary row lock wait for anyone reading pg_locks.
    /// </summary>
    private Task LockResourceAsync(Guid resourceId, CancellationToken ct)
        => db.Database.ExecuteSqlAsync(
            $"SELECT 1 FROM resources WHERE id = {resourceId} FOR NO KEY UPDATE", ct);

    private Task<Reservation?> FindOverlapAsync(Guid resourceId, NpgsqlRange<DateTime> during, CancellationToken ct)
        => db.Reservations.FirstOrDefaultAsync(
            r => r.ResourceId == resourceId
                 && r.Status == ReservationStatus.Confirmed
                 && r.During.Overlaps(during),
            ct);

    /// <summary>
    /// Whether a 40P01 deadlock is anywhere in the exception's chain.
    ///
    /// It never arrives as a bare <see cref="DbUpdateException"/>, which is why the clause
    /// above cannot catch it however many SqlStates are added to it. Npgsql classifies
    /// deadlock as transient, so EF Core hands it back wrapped in
    /// <c>InvalidOperationException("...likely due to a transient failure")</c> — and in
    /// <c>RetryLimitExceededException</c> instead, if a retrying execution strategy is ever
    /// configured on the context.
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
