using System.Security.Claims;
using LabQueue.Api.Auth;
using LabQueue.Api.Contracts;
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
        var group = app.MapGroup("/reservations").WithTags("Reservations").RequireAuthorization();

        group.MapPost("/", BookAsync).WithValidation<CreateReservationRequest>();
        group.MapGet("/", ListMineAsync);
        group.MapDelete("/{id:guid}", CancelAsync);

        return app;
    }

    private static async Task<IResult> BookAsync(
        CreateReservationRequest request,
        ClaimsPrincipal principal,
        ReservationService reservations,
        CancellationToken ct)
    {
        var result = await reservations.BookAsync(
            principal.UserId(), request.ResourceId, request.From, request.To, ct);

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
