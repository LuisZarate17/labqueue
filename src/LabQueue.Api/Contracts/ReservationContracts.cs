namespace LabQueue.Api.Contracts;

public sealed record CreateReservationRequest(Guid ResourceId, DateTimeOffset From, DateTimeOffset To);

public sealed record ReservationResponse(
    Guid Id,
    Guid ResourceId,
    Guid UserId,
    DateTimeOffset From,
    DateTimeOffset To,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CancelledAt);
