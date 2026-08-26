namespace LabQueue.Api.Contracts;

public sealed record CreateResourceRequest(
    string Code,
    string Name,
    string? Location,
    string? Description,
    Guid? RequiredCertificationId);

public sealed record CreateMaintenanceWindowRequest(
    Guid ResourceId,
    DateTimeOffset From,
    DateTimeOffset To,
    string? Reason);

public sealed record GrantCertificationRequest(Guid CertificationId, DateTimeOffset? ExpiresAt);

public sealed record UserCertificationResponse(
    Guid UserId,
    Guid CertificationId,
    string CertificationCode,
    DateTimeOffset GrantedAt,
    DateTimeOffset? ExpiresAt);
