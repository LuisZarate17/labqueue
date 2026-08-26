using FluentValidation;
using LabQueue.Api.Contracts;

namespace LabQueue.Api.Validation;

/// <summary>
/// Structural checks only. The semantics of the window — ordering, duration,
/// and the conflict rules — are enforced by the booking service, which applies
/// them in a defined order relative to the resource checks.
/// </summary>
public sealed class CreateReservationRequestValidator : AbstractValidator<CreateReservationRequest>
{
    public CreateReservationRequestValidator()
    {
        RuleFor(x => x.ResourceId).NotEmpty();
        RuleFor(x => x.From).NotEmpty();
        RuleFor(x => x.To).NotEmpty();
    }
}
