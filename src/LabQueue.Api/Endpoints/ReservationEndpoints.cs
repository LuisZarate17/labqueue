using System.Security.Claims;
using LabQueue.Api.Auth;
using LabQueue.Api.Contracts;
using LabQueue.Api.Observability;
using LabQueue.Api.Validation;
using LabQueue.Core.Data;
using LabQueue.Core.Entities;
using LabQueue.Core.Enums;
using LabQueue.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace LabQueue.Api.Endpoints;

public static class ReservationEndpoints
{
    public static IEndpointRouteBuilder MapReservationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/reservations")
                       .WithTags("Reservations")
                       .RequireAuthorization()
                       .ProducesProblem(StatusCodes.Status401Unauthorized);

        group.MapPost("/", BookAsync)
             .WithValidation<CreateReservationRequest>()
             .WithName("BookReservation")
             .WithSummary("Book an instrument for a window")
             .WithDescription(
                 "Five rules, enforced in this order:\n\n"
                 + "1. the resource exists (404) and is active (409)\n"
                 + "2. the window is well formed — 'to' after 'from', between 15 minutes and "
                 + "8 hours long (400)\n"
                 + "3. the caller holds the resource's required certification, unexpired (403)\n"
                 + "4. no maintenance window overlaps (409)\n"
                 + "5. no confirmed reservation overlaps (409)\n\n"
                 + "Send the same request twice to see rule 5. The second one is refused by "
                 + "reservations_no_overlap, a partial GiST exclusion constraint, rather than by "
                 + "the SELECT that precedes the insert — which is the point of Finding A in the "
                 + "repository README. Cancelling frees the slot, because the constraint is "
                 + "partial on status = 'confirmed'.")
             .Produces<ReservationResponse>(StatusCodes.Status201Created)
             .ProducesProblem(StatusCodes.Status403Forbidden)
             .ProducesProblem(StatusCodes.Status404NotFound)
             .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapGet("/", ListMineAsync)
             .WithName("ListMyReservations")
             .WithSummary("List your own reservations")
             .WithDescription(
                 "Scoped to the token's user — there is no route to anyone else's. Optionally "
                 + "filtered by status, 'confirmed' or 'cancelled'.")
             .Produces<IReadOnlyList<ReservationResponse>>()
             .ProducesValidationProblem();

        group.MapDelete("/{id:guid}", CancelAsync)
             .WithName("CancelReservation")
             .WithSummary("Cancel a reservation and free the slot")
             .WithDescription(
                 "Cancellable by the person who made it, or by an admin. The row is kept and "
                 + "marked cancelled rather than deleted, and the slot becomes bookable again "
                 + "immediately.")
             .Produces(StatusCodes.Status204NoContent)
             .ProducesProblem(StatusCodes.Status403Forbidden)
             .ProducesProblem(StatusCodes.Status404NotFound)
             .ProducesProblem(StatusCodes.Status409Conflict);

        return app;
    }

    private static async Task<IResult> BookAsync(
        CreateReservationRequest request,
        ClaimsPrincipal principal,
        ReservationService reservations,
        LabQueueMetrics metrics,
        CancellationToken ct)
    {
        var result = await reservations.BookAsync(
            principal.UserId(), request.ResourceId, request.From, request.To, ct);

        if (result.Outcome == BookingOutcome.ReservationConflict)
        {
            // Only this outcome. ResourceNotActive and MaintenanceConflict are 409s as well,
            // but they are not the overlap check, and counting them here would blunt the
            // signal this instrument exists to carry.
            metrics.ReservationConflict();
        }

        return result.Outcome switch
        {
            BookingOutcome.Created =>
                Results.Created($"/reservations/{result.Reservation!.Id}", ToResponse(result.Reservation)),

            BookingOutcome.ResourceNotFound => Results.Problem(
                title: "Resource not found", detail: result.Detail,
                statusCode: StatusCodes.Status404NotFound),

            BookingOutcome.ResourceNotActive => Results.Problem(
                title: "Resource not bookable", detail: result.Detail,
                statusCode: StatusCodes.Status409Conflict),

            BookingOutcome.InvalidWindow => Results.Problem(
                title: "Invalid reservation window", detail: result.Detail,
                statusCode: StatusCodes.Status400BadRequest),

            BookingOutcome.CertificationRequired => Results.Problem(
                title: "Certification required", detail: result.Detail,
                statusCode: StatusCodes.Status403Forbidden),

            BookingOutcome.MaintenanceConflict => Results.Problem(
                title: "Resource under maintenance", detail: result.Detail,
                statusCode: StatusCodes.Status409Conflict),

            BookingOutcome.ReservationConflict => Results.Problem(
                title: "Reservation conflict", detail: result.Detail,
                statusCode: StatusCodes.Status409Conflict),

            _ => Results.Problem(statusCode: StatusCodes.Status500InternalServerError)
        };
    }

    private static async Task<IResult> ListMineAsync(
        ClaimsPrincipal principal,
        LabQueueDbContext db,
        CancellationToken ct,
        string? status = null,
        int skip = 0,
        int take = 50)
    {
        take = Math.Clamp(take, 1, 200);
        skip = Math.Max(skip, 0);

        var userId = principal.UserId();
        var query = db.Reservations.AsNoTracking().Where(r => r.UserId == userId);

        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse<ReservationStatus>(status, ignoreCase: true, out var parsed))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["status"] = ["Status must be 'confirmed' or 'cancelled'."]
                });
            }

            query = query.Where(r => r.Status == parsed);
        }

        var reservations = await query
            .OrderByDescending(r => r.CreatedAt)
            .Skip(skip)
            .Take(take)
            .ToListAsync(ct);

        return Results.Ok(reservations.Select(ToResponse).ToList());
    }

    private static async Task<IResult> CancelAsync(
        Guid id,
        ClaimsPrincipal principal,
        ReservationService reservations,
        CancellationToken ct)
    {
        var outcome = await reservations.CancelAsync(id, principal.UserId(), principal.IsAdmin(), ct);

        return outcome switch
        {
            CancellationOutcome.Cancelled => Results.NoContent(),

            CancellationOutcome.NotFound => Results.Problem(
                title: "Reservation not found",
                detail: $"No reservation exists with id {id}.",
                statusCode: StatusCodes.Status404NotFound),

            CancellationOutcome.NotOwned => Results.Problem(
                title: "Not your reservation",
                detail: "A reservation can only be cancelled by the person who made it, or by an administrator.",
                statusCode: StatusCodes.Status403Forbidden),

            CancellationOutcome.AlreadyCancelled => Results.Problem(
                title: "Already cancelled",
                detail: "That reservation has already been cancelled.",
                statusCode: StatusCodes.Status409Conflict),

            _ => Results.Problem(statusCode: StatusCodes.Status500InternalServerError)
        };
    }

    internal static ReservationResponse ToResponse(Reservation reservation) => new(
        reservation.Id,
        reservation.ResourceId,
        reservation.UserId,
        reservation.During.LowerBound,
        reservation.During.UpperBound,
        reservation.Status.ToString().ToLowerInvariant(),
        reservation.CreatedAt,
        reservation.CancelledAt);
}
