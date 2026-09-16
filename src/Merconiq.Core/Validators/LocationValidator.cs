using FluentValidation;
using Merconiq.Core.Entities;

namespace Merconiq.Core.Validators;

public class LocationValidator : AbstractValidator<Location>
{
    public LocationValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("Location name is required")
            .MaximumLength(200).WithMessage("Location name must not exceed 200 characters");

        RuleFor(x => x.Address)
            .MaximumLength(500).WithMessage("Address must not exceed 500 characters");
    }
}
