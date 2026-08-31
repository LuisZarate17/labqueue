using LabQueue.Api.Contracts;
using LabQueue.Core.Data;
using LabQueue.Core.Entities;
using LabQueue.Core.Enums;
using LabQueue.Core.Time;
using Microsoft.EntityFrameworkCore;

namespace LabQueue.Api.Endpoints;

public static class ResourceEndpoints
{
    /// <summary>The widest availability window a single request may ask for.</summary>
    public static readonly TimeSpan MaximumAvailabilityWindow = TimeSpan.FromDays(31);

    public static IEndpointRouteBuilder MapResourceEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/resources")
                       .WithTags("Resources")
                       .RequireAuthorization()
                       .ProducesProblem(StatusCodes.Status401Unauthorized);

        group.MapGet("/", ListAsync)
             .WithName("ListResources")
             .WithSummary("List bookable instruments")
             .WithDescription(
                 "Active resources only unless includeRetired is set. Start here: the id of a "
                 + "resource is what POST /reservations wants.")
             .Produces<IReadOnlyList<ResourceResponse>>();

        group.MapGet("/{id:guid}", GetAsync)
             .WithName("GetResource")
             .WithSummary("Fetch one instrument")
             .WithDescription(
                 "requiredCertification is the gate: a resource that has one can only be booked "
                 + "by a caller holding it and unexpired, which an admin grants.")
             .Produces<ResourceResponse>()
             .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/{id:guid}/availability", GetAvailabilityAsync)
             .WithName("GetResourceAvailability")
             .WithSummary("What is already taken in a window")
             .WithDescription(
                 "from and to are both required, and 'to' must be later than 'from'. The window "
                 + $"may not exceed {MaximumAvailabilityWindow.TotalDays:0} days.\n\n"
                 + "Returns what is busy, not what is free: the confirmed reservations and the "
                 + "maintenance windows overlapping the range. The caller works out the gaps, "
                 + "because computing them here would mean computing them in the database on "
                 + "every request.\n\n"
                 + "This is the query behind Finding B — see the repository README.")
             .Produces<AvailabilityResponse>()
             .ProducesValidationProblem()
             .ProducesProblem(StatusCodes.Status404NotFound);

        return app;
    }

    private static async Task<IResult> ListAsync(
        LabQueueDbContext db,
        CancellationToken ct,
        bool includeRetired = false,
        int skip = 0,
        int take = 50)
    {
        take = Math.Clamp(take, 1, 200);
        skip = Math.Max(skip, 0);

        var query = db.Resources.AsNoTracking().Include(r => r.RequiredCertification).AsQueryable();
        if (!includeRetired)
        {
            query = query.Where(r => r.Status == ResourceStatus.Active);
        }

        var resources = await query.OrderBy(r => r.Code).Skip(skip).Take(take).ToListAsync(ct);
        return Results.Ok(resources.Select(ToResponse).ToList());
    }

    private static async Task<IResult> GetAsync(Guid id, LabQueueDbContext db, CancellationToken ct)
    {
        var resource = await db.Resources.AsNoTracking()
            .Include(r => r.RequiredCertification)
            .FirstOrDefaultAsync(r => r.Id == id, ct);

        return resource is null ? ResourceNotFound(id) : Results.Ok(ToResponse(resource));
    }

    private static async Task<IResult> GetAvailabilityAsync(
        Guid id,
        DateTimeOffset from,
        DateTimeOffset to,
        LabQueueDbContext db,
        CancellationToken ct)
    {
        if (to <= from)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["to"] = ["'to' must be later than 'from'."]
            });
        }

        if (to - from > MaximumAvailabilityWindow)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["to"] = [$"The availability window may not exceed {MaximumAvailabilityWindow.TotalDays:0} days."]
            });
        }

        if (!await db.Resources.AnyAsync(r => r.Id == id, ct))
        {
            return ResourceNotFound(id);
        }

        var window = TimeWindow.ClosedOpen(from, to);

        var reservations = await db.Reservations.AsNoTracking()
            .Where(r => r.ResourceId == id
                        && r.Status == ReservationStatus.Confirmed
                        && r.During.Overlaps(window))
            .ToListAsync(ct);

        var maintenance = await db.MaintenanceWindows.AsNoTracking()
            .Where(m => m.ResourceId == id && m.During.Overlaps(window))
            .ToListAsync(ct);

        return Results.Ok(new AvailabilityResponse(
            id,
            from,
            to,
            reservations
                .Select(r => new BusyWindow(r.During.LowerBound, r.During.UpperBound))
                .OrderBy(w => w.From)
                .ToList(),
            maintenance
                .Select(m => new MaintenanceWindowResponse(m.Id, m.During.LowerBound, m.During.UpperBound, m.Reason))
                .OrderBy(w => w.From)
                .ToList()));
    }

    internal static IResult ResourceNotFound(Guid id) => Results.Problem(
        title: "Resource not found",
        detail: $"No resource exists with id {id}.",
        statusCode: StatusCodes.Status404NotFound);

    internal static ResourceResponse ToResponse(Resource resource) => new(
        resource.Id,
        resource.Code,
        resource.Name,
        resource.Location,
        resource.Description,
        resource.Status.ToString().ToLowerInvariant(),
        resource.RequiredCertification is null
            ? null
            : new CertificationSummary(
                resource.RequiredCertification.Id,
                resource.RequiredCertification.Code,
                resource.RequiredCertification.Name));
}
