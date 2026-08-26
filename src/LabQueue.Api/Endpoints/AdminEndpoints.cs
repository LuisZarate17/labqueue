using LabQueue.Api.Contracts;
using LabQueue.Api.Validation;
using LabQueue.Core.Data;
using LabQueue.Core.Entities;
using LabQueue.Core.Enums;
using LabQueue.Core.Time;
using Microsoft.EntityFrameworkCore;

namespace LabQueue.Api.Endpoints;

public static class AdminEndpoints
{
    public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup(string.Empty).WithTags("Admin").RequireAuthorization("admin");

        admin.MapPost("/resources", CreateResourceAsync)
             .WithValidation<CreateResourceRequest>();
        admin.MapPost("/maintenance-windows", CreateMaintenanceWindowAsync)
             .WithValidation<CreateMaintenanceWindowRequest>();
        admin.MapPost("/users/{id:guid}/certifications", GrantCertificationAsync)
             .WithValidation<GrantCertificationRequest>();

        return app;
    }

    private static async Task<IResult> CreateResourceAsync(
        CreateResourceRequest request, LabQueueDbContext db, CancellationToken ct)
    {
        var code = request.Code.Trim();

        if (await db.Resources.AnyAsync(r => r.Code == code, ct))
        {
            return Results.Problem(
                title: "Resource code already in use",
                detail: $"A resource with code {code} already exists.",
                statusCode: StatusCodes.Status409Conflict);
        }

        if (request.RequiredCertificationId is { } certificationId
            && !await db.Certifications.AnyAsync(c => c.Id == certificationId, ct))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["requiredCertificationId"] = ["No certification exists with that id."]
            });
        }

        var resource = new Resource
        {
            Id = Guid.CreateVersion7(),
            Code = code,
            Name = request.Name.Trim(),
            Location = request.Location?.Trim(),
            Description = request.Description?.Trim(),
            RequiredCertificationId = request.RequiredCertificationId,
            Status = ResourceStatus.Active,
            CreatedAt = DateTime.UtcNow
        };

        db.Resources.Add(resource);
        await db.SaveChangesAsync(ct);

        await db.Entry(resource).Reference(r => r.RequiredCertification).LoadAsync(ct);
        return Results.Created($"/resources/{resource.Id}", ResourceEndpoints.ToResponse(resource));
    }

    private static async Task<IResult> CreateMaintenanceWindowAsync(
        CreateMaintenanceWindowRequest request, LabQueueDbContext db, CancellationToken ct)
    {
        if (!await db.Resources.AnyAsync(r => r.Id == request.ResourceId, ct))
        {
            return ResourceEndpoints.ResourceNotFound(request.ResourceId);
        }

        var window = new MaintenanceWindow
        {
            Id = Guid.CreateVersion7(),
            ResourceId = request.ResourceId,
            During = TimeWindow.ClosedOpen(request.From, request.To),
            Reason = request.Reason?.Trim(),
            CreatedAt = DateTime.UtcNow
        };

        db.MaintenanceWindows.Add(window);
        await db.SaveChangesAsync(ct);

        return Results.Created($"/maintenance-windows/{window.Id}", new MaintenanceWindowResponse(
            window.Id, window.During.LowerBound, window.During.UpperBound, window.Reason));
    }

    private static async Task<IResult> GrantCertificationAsync(
        Guid id, GrantCertificationRequest request, LabQueueDbContext db, CancellationToken ct)
    {
        if (!await db.Users.AnyAsync(u => u.Id == id, ct))
        {
            return Results.Problem(
                title: "User not found",
                detail: $"No user exists with id {id}.",
                statusCode: StatusCodes.Status404NotFound);
        }

        var certification = await db.Certifications
            .FirstOrDefaultAsync(c => c.Id == request.CertificationId, ct);

        if (certification is null)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["certificationId"] = ["No certification exists with that id."]
            });
        }

        var grant = await db.UserCertifications
            .FirstOrDefaultAsync(uc => uc.UserId == id && uc.CertificationId == certification.Id, ct);

        if (grant is null)
        {
            grant = new UserCertification
            {
                UserId = id,
                CertificationId = certification.Id,
                GrantedAt = DateTime.UtcNow
            };
            db.UserCertifications.Add(grant);
        }

        grant.ExpiresAt = request.ExpiresAt?.UtcDateTime;
        await db.SaveChangesAsync(ct);

        return Results.Ok(new UserCertificationResponse(
            grant.UserId, grant.CertificationId, certification.Code, grant.GrantedAt, grant.ExpiresAt));
    }
}
