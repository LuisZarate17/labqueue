using FluentValidation;
using LabQueue.Api.Contracts;

namespace LabQueue.Api.Validation;

public sealed class CreateResourceRequestValidator : AbstractValidator<CreateResourceRequest>
{
    public CreateResourceRequestValidator()
    {
        RuleFor(x => x.Code).NotEmpty().MaximumLength(50);
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Location).MaximumLength(200);
    }
}

public sealed class CreateMaintenanceWindowRequestValidator : AbstractValidator<CreateMaintenanceWindowRequest>
{
    public CreateMaintenanceWindowRequestValidator()
    {
        RuleFor(x => x.ResourceId).NotEmpty();
        RuleFor(x => x.To).GreaterThan(x => x.From)
            .WithMessage("'to' must be later than 'from'.");
        RuleFor(x => x.Reason).MaximumLength(500);
    }
}

public sealed class GrantCertificationRequestValidator : AbstractValidator<GrantCertificationRequest>
{
    public GrantCertificationRequestValidator()
    {
        RuleFor(x => x.CertificationId).NotEmpty();
    }
}
