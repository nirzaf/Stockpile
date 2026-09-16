using FluentValidation;
using Merconiq.Core.Entities;

namespace Merconiq.Core.Validators;

public class ItemValidator : AbstractValidator<Item>
{
    public ItemValidator()
    {
        RuleFor(x => x.ItemCode)
            .NotEmpty().WithMessage("Item code is required")
            .MaximumLength(50).WithMessage("Item code must not exceed 50 characters");

        RuleFor(x => x.Description)
            .MaximumLength(500).WithMessage("Description must not exceed 500 characters");

        RuleFor(x => x.Barcode)
            .MaximumLength(100).WithMessage("Barcode must not exceed 100 characters");

        RuleFor(x => x.Rate)
            .GreaterThan(0).WithMessage("Rate must be greater than zero");
    }
}
