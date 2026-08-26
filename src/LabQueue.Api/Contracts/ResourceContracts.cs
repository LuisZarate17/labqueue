namespace LabQueue.Api.Contracts;

public sealed record CertificationSummary(Guid Id, string Code, string Name);

public sealed record ResourceResponse(
    Guid Id,
    string Code,
    string Name,
    string? Location,
    string? Description,
    string Status,
    CertificationSummary? RequiredCertification);

public sealed record BusyWindow(DateTimeOffset From, DateTimeOffset To);

public sealed record MaintenanceWindowResponse(Guid Id, DateTimeOffset From, DateTimeOffset To, string? Reason);

/// <summary>
/// What is booked or unavailable inside the requested window. The caller works out
/// the gaps; returning them from here would mean computing them in the database on
/// every request.
/// </summary>
public sealed record AvailabilityResponse(
    Guid ResourceId,
    DateTimeOffset From,
    DateTimeOffset To,
    IReadOnlyList<BusyWindow> Reservations,
    IReadOnlyList<MaintenanceWindowResponse> MaintenanceWindows);
